using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class EmailOtpService : IEmailOtpService
{
    private const int MaxVerificationAttempts = 5;
    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailOtpService> _logger;

    public EmailOtpService(
        AppDbContext dbContext,
        IConfiguration configuration,
        IEmailService emailService,
        ILogger<EmailOtpService> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<SendEmailOtpResponseDto> SendEmailOtpAsync(
        SendEmailOtpRequestDto request,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        var purpose = NormalizePurpose(request.Purpose);

        var cooldownSeconds = _configuration.GetValue<int>("Otp:ResendCooldownSeconds", 60);
        var expiryMinutes = _configuration.GetValue<int>("Otp:ExpirationMinutes", 5);

        var now = DateTime.UtcNow;

        // 1. Check Resend Cooldown
        var recentOtp = await _dbContext.EmailOtpRecords
            .Where(x => x.Email == email && x.Purpose == purpose && !x.IsUsed)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (recentOtp != null && recentOtp.CreatedAt.AddSeconds(cooldownSeconds) > now)
        {
            var remainingSeconds = (int)Math.Ceiling((recentOtp.CreatedAt.AddSeconds(cooldownSeconds) - now).TotalSeconds);
            throw new InvalidOperationException($"Please wait {remainingSeconds} seconds before requesting a new verification code.");
        }

        // 2. Invalidate previous active OTPs for same email + purpose
        var activeOtps = await _dbContext.EmailOtpRecords
            .Where(x => x.Email == email && x.Purpose == purpose && !x.IsUsed)
            .ToListAsync(cancellationToken);

        foreach (var activeOtp in activeOtps)
        {
            activeOtp.IsUsed = true;
        }

        // 3. Generate Cryptographically Secure 6-digit OTP
        var rawOtp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        var otpHash = ComputeOtpHash(rawOtp, email, purpose);

        // 4. Store Hash & Metadata in DB
        var otpRecord = new EmailOtpRecord
        {
            Id = Guid.NewGuid(),
            Email = email,
            OtpHash = otpHash,
            Purpose = purpose,
            ExpiresAt = now.AddMinutes(expiryMinutes),
            CreatedAt = now,
            AttemptCount = 0,
            IsUsed = false,
            RequestIp = clientIp
        };

        _dbContext.EmailOtpRecords.Add(otpRecord);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 5. Build HTML & Text Email Templates
        var htmlContent = BuildHtmlTemplate(rawOtp, expiryMinutes);
        var textContent = BuildTextTemplate(rawOtp, expiryMinutes);

        // 6. Send via Amazon SES
        await _emailService.SendEmailAsync(
            email,
            "Your Propseek verification code",
            htmlContent,
            textContent,
            cancellationToken);

        // 7. Return Generic Response (Prevents Account Enumeration)
        return new SendEmailOtpResponseDto
        {
            Success = true,
            Message = "If the email is valid, a verification code has been sent."
        };
    }

    public async Task<VerifyEmailOtpResponseDto> VerifyEmailOtpAsync(
        VerifyEmailOtpRequestDto request,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        var submittedOtp = request.Otp.Trim();
        var purpose = NormalizePurpose(request.Purpose);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var now = DateTime.UtcNow;

            var otpRecord = await _dbContext.EmailOtpRecords
                .Where(x => x.Email == email && x.Purpose == purpose)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (otpRecord == null)
            {
                throw new InvalidOperationException("No verification request found for this email.");
            }

            if (otpRecord.IsUsed)
            {
                throw new InvalidOperationException("This verification code has already been used.");
            }

            if (otpRecord.ExpiresAt < now)
            {
                throw new InvalidOperationException("Verification code has expired. Please request a new one.");
            }

            if (otpRecord.AttemptCount >= MaxVerificationAttempts)
            {
                otpRecord.IsUsed = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw new InvalidOperationException("Maximum verification attempts exceeded. Please request a new verification code.");
            }

            var expectedHash = ComputeOtpHash(submittedOtp, email, purpose);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(otpRecord.OtpHash),
                    Encoding.UTF8.GetBytes(expectedHash)))
            {
                otpRecord.AttemptCount += 1;
                if (otpRecord.AttemptCount >= MaxVerificationAttempts)
                {
                    otpRecord.IsUsed = true;
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw new InvalidOperationException("Invalid verification code. Please try again.");
            }

            // OTP verified successfully
            otpRecord.IsUsed = true;
            otpRecord.UsedAt = now;

            // Invalidate any remaining active OTPs for email + purpose
            var activeOtps = await _dbContext.EmailOtpRecords
                .Where(x => x.Email == email && x.Purpose == purpose && !x.IsUsed && x.Id != otpRecord.Id)
                .ToListAsync(cancellationToken);

            foreach (var activeOtp in activeOtps)
            {
                activeOtp.IsUsed = true;
            }

            // Authentication System Integration
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken);
            if (user != null)
            {
                user.IsEmailVerified = true;
                user.ModifiedDate = now;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            string? token = null;
            string? refreshToken = null;
            DateTime? expiresAt = null;
            AuthenticatedUserDto? userDto = null;

            if (user != null)
            {
                token = GenerateJwtToken(user, out var expDate);
                refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
                expiresAt = expDate;

                userDto = new AuthenticatedUserDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    MobileNumber = user.MobileNumber,
                    Email = user.Email,
                    IsMobileVerified = user.IsMobileVerified,
                    Credits = user.Credits
                };
            }

            return new VerifyEmailOtpResponseDto
            {
                Success = true,
                Message = "Email verification successful.",
                Token = token,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                User = userDto
            };
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch
            {
                // Ignore rollback exceptions if the transaction was already committed in the try block
            }
            throw;
        }
    }

    private string ComputeOtpHash(string rawOtp, string email, string purpose)
    {
        var secretKey = _configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("Jwt:Key is not configured.");
        }

        var payload = $"{email}:{purpose}:{rawOtp}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hashBytes);
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");
        return email.Trim().ToLowerInvariant();
    }

    private static string NormalizePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return "EmailVerification";

        var normalized = purpose.Trim();
        return normalized switch
        {
            "Login" => "Login",
            "PasswordReset" => "PasswordReset",
            _ => "EmailVerification"
        };
    }

    private string GenerateJwtToken(User user, out DateTime expiresAt)
    {
        var jwtKey = _configuration["Jwt:Key"];
        if (string.IsNullOrEmpty(jwtKey))
        {
            throw new InvalidOperationException("Jwt:Key is not configured.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresMinutes = _configuration.GetValue<int>("Jwt:ExpiresMinutes", 60);
        expiresAt = DateTime.UtcNow.AddMinutes(expiresMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.MobilePhone, user.MobileNumber),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty)
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string BuildHtmlTemplate(string otp, int expiryMinutes)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Propseek Verification Code</title>
</head>
<body style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f4f6f9; margin: 0; padding: 20px;"">
    <div style=""max-width: 500px; margin: 0 auto; background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 12px rgba(0,0,0,0.08); border: 1px solid #e1e4e8;"">
        <div style=""background-color: #0F172A; padding: 24px; text-align: center;"">
            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; font-weight: 700; letter-spacing: 0.5px;"">Propseek</h1>
        </div>
        <div style=""padding: 32px 24px;"">
            <p style=""font-size: 16px; color: #334155; margin-top: 0; margin-bottom: 16px;"">Hi,</p>
            <p style=""font-size: 15px; color: #334155; margin-bottom: 24px;"">Use the following verification code to continue with Propseek:</p>
            
            <div style=""background-color: #F8FAFC; border: 1px dashed #CBD5E1; border-radius: 8px; padding: 20px; text-align: center; margin-bottom: 24px;"">
                <span style=""font-family: 'Courier New', Courier, monospace; font-size: 36px; font-weight: 800; color: #0284C7; letter-spacing: 8px;"">{otp}</span>
            </div>

            <p style=""font-size: 14px; color: #64748B; margin-bottom: 16px;"">This code expires in <strong>{expiryMinutes} minutes</strong>.</p>
            <p style=""font-size: 14px; color: #64748B; margin-bottom: 24px;"">Do not share this code with anyone.</p>
            <p style=""font-size: 13px; color: #94A3B8; margin-bottom: 0;"">If you did not request this code, you can safely ignore this email.</p>
        </div>
        <div style=""background-color: #F1F5F9; padding: 16px 24px; text-align: center; border-top: 1px solid #E2E8F0;"">
            <p style=""font-size: 12px; color: #64748B; margin: 0;"">&copy; Propseek Team &bull; propseekr.com</p>
        </div>
    </div>
</body>
</html>";
    }

    private static string BuildTextTemplate(string otp, int expiryMinutes)
    {
        return $@"Hi,

Use the following verification code to continue with Propseek:

{otp}

This code expires in {expiryMinutes} minutes.

Do not share this code with anyone.

If you did not request this code, you can safely ignore this email.

Propseek Team
propseekr.com";
    }
}
