using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace PropSeekr.Services;

public class AuthService : IAuthService
{
    private const int PasswordSaltSize = 16;
    private const int PasswordHashSize = 32;
    private const int PasswordHashIterations = 100000;

    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IOtpDeliveryService _otpDeliveryService;
    private readonly IServiceProvider _serviceProvider;

    public AuthService(
        AppDbContext dbContext,
        IConfiguration configuration,
        IOtpDeliveryService otpDeliveryService,
        IServiceProvider serviceProvider)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _otpDeliveryService = otpDeliveryService;
        _serviceProvider = serviceProvider;
    }

    public async Task<AdminLoginResponseDto> AdminLoginAsync(AdminLoginRequestDto request)
    {
        var userName = NormalizeRequired(request.UserName, "Username");
        var password = request.Password;

        var admin = await _dbContext.AdminUsers.FirstOrDefaultAsync(a => a.UserName == userName && a.IsActive);
        if (admin == null || !VerifyPassword(password, admin.PasswordHash))
        {
            throw new Exception("Invalid username or password.");
        }

        var token = GenerateAdminJwtToken(admin, out var expiresAt);

        return new AdminLoginResponseDto
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserName = admin.UserName
        };
    }

    public async Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        var mobile = NormalizeRequired(request.Mobile, "Mobile number");
        var email = NormalizeRequired(request.Email, "Email").ToLowerInvariant();
        var name = NormalizeRequired(request.Name, "Name");
        var password = request.Password;
        var aadharNumber = NormalizeRequired(request.AadharNumber, "Aadhar number");
        var panCard = NormalizeRequired(request.PanCard, "PAN card").ToUpperInvariant();
        var gstNumber = NormalizeOptional(request.GstNumber)?.ToUpperInvariant();
        var reraNumber = NormalizeOptional(request.ReraRegistrationNumber)?.ToUpperInvariant();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        await EnsureRegistrationIsUniqueAsync(mobile, email, aadharNumber, panCard);

        var passwordHash = HashPassword(password);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = name,
            MobileNumber = mobile,
            Email = email,
            PasswordHash = passwordHash,
            AadharNumber = aadharNumber,
            PanCard = panCard,
            GSTNumber = gstNumber,
            ReraRegistrationNumber = reraNumber,
            IsMobileVerified = false,
            Credits = 0,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        await CreateOtpAsync(mobile, "Registration successful. OTP verification is pending.");

        await transaction.CommitAsync();

        // Automatically trigger Email OTP in the background
        var serviceProvider = _serviceProvider;
        _ = Task.Run(async () =>
        {
            using (var scope = serviceProvider.CreateScope())
            {
                try
                {
                    var emailOtpService = scope.ServiceProvider.GetRequiredService<IEmailOtpService>();
                    await emailOtpService.SendEmailOtpAsync(new SendEmailOtpRequestDto
                    {
                        Email = email,
                        Purpose = "EmailVerification"
                    }, clientIp: null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Email Error] Failed to send registration email to {email}: {ex}");
                }
            }
        });

        return new RegisterResponseDto
        {
            UserId = user.Id,
            Message = "Registration successful. OTP verification is pending."
        };
    }

    public async Task<LoginResponseDto> LoginAsync(LoginRequestDto request)
    {
        var identifier = NormalizeRequired(request.Identifier, "Mobile number or Email").ToLowerInvariant();
        var password = request.Password;

        // Find user by Mobile Number OR Email Address
        var user = await _dbContext.Users.FirstOrDefaultAsync(u =>
            u.MobileNumber == identifier ||
            (u.Email != null && u.Email.ToLower() == identifier));

        if (user == null || !VerifyPassword(password, user.PasswordHash))
        {
            throw new Exception("Invalid mobile number/email or password.");
        }

        var token = GenerateJwtToken(user, out var expiresAt);
        var refreshToken = GenerateRefreshToken();

        return new LoginResponseDto
        {
            Success = true,
            Message = "Login successful.",
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            User = new AuthenticatedUserDto
            {
                Id = user.Id,
                Name = user.Name,
                MobileNumber = user.MobileNumber,
                Email = user.Email,
                IsMobileVerified = user.IsMobileVerified,
                Credits = user.Credits
            }
        };
    }

    public async Task<OtpResponseDto> SendOtpAsync(SendOtpRequestDto request)
    {
        return await CreateOtpAsync(request.MobileNumber, "OTP sent successfully.");
    }

    public async Task<OtpResponseDto> ResendOtpAsync(SendOtpRequestDto request)
    {
        return await CreateOtpAsync(request.MobileNumber, "OTP resent successfully.");
    }

    public Task<LogoutResponseDto> LogoutAsync()
    {
        return Task.FromResult(new LogoutResponseDto
        {
            Message = "Logout successful."
        });
    }

    private async Task EnsureRegistrationIsUniqueAsync(
        string mobileNumber,
        string email,
        string aadharNumber,
        string panCard)
    {
        if (await _dbContext.Users.AnyAsync(x => x.MobileNumber == mobileNumber))
        {
            throw new Exception("Mobile number already registered.");
        }

        if (await _dbContext.Users.AnyAsync(x => x.Email != null && x.Email.ToLower() == email))
        {
            throw new Exception("Email already registered.");
        }

        if (await _dbContext.Users.AnyAsync(x => x.AadharNumber == aadharNumber))
        {
            throw new Exception("Aadhar number already registered.");
        }

        if (await _dbContext.Users.AnyAsync(x => x.PanCard == panCard))
        {
            throw new Exception("PAN card already registered.");
        }
    }

    private async Task<OtpResponseDto> CreateOtpAsync(
        string mobileNumber,
        string message)
    {
        if (!await _dbContext.Users.AnyAsync(x => x.MobileNumber == mobileNumber))
        {
            throw new Exception("Mobile number is not registered.");
        }

        var now = DateTime.UtcNow;

        var activeOtps = await _dbContext.OtpVerifications
            .Where(x =>
                x.MobileNumber == mobileNumber &&
                !x.IsUsed)
            .ToListAsync();

        foreach (var activeOtp in activeOtps)
        {
            activeOtp.IsUsed = true;
        }

        var otp = GenerateOtp();
        var expiryMinutes = _configuration.GetValue<int>("Otp:ExpiryMinutes", 5);

        var otpEntity = new OtpVerification
        {
            Id = Guid.NewGuid(),
            MobileNumber = mobileNumber,
            OtpCode = otp,
            IsUsed = false,
            CreatedDate = now,
            ExpiresAt = now.AddMinutes(expiryMinutes)
        };

        _dbContext.OtpVerifications.Add(otpEntity);
        await _dbContext.SaveChangesAsync();

        // Send OTP via SMS service (MSG91 if configured, or local fallback)
        await _otpDeliveryService.SendOtpAsync(mobileNumber, otp);

        return new OtpResponseDto
        {
            Status = "SUCCESS",
            Message = message,
            ExpiryMinutes = expiryMinutes,
            ExpiresAt = otpEntity.ExpiresAt
        };
    }

    public async Task<VerifyOtpResponseDto> VerifyOtpAsync(VerifyOtpRequestDto request)
    {
        var mobileNumber = NormalizeRequired(request.Mobile, "Mobile number");
        var otpCode = NormalizeRequired(request.Otp, "OTP");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            var otp = await _dbContext.OtpVerifications
                .Where(o =>
                    o.MobileNumber == mobileNumber &&
                    o.OtpCode == otpCode &&
                    !o.IsUsed &&
                    o.ExpiresAt >= DateTime.UtcNow)
                .OrderByDescending(o => o.CreatedDate)
                .FirstOrDefaultAsync();

            if (otp == null)
            {
                throw new Exception("Invalid or expired OTP.");
            }

            otp.IsUsed = true;
            await _dbContext.SaveChangesAsync();

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.MobileNumber == mobileNumber);

            if (user == null)
            {
                throw new Exception("User not found for the provided mobile number.");
            }

            user.IsMobileVerified = true;
            user.ModifiedDate = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            var token = GenerateJwtToken(user, out var expiresAt);

            return new VerifyOtpResponseDto
            {
                Token = token,
                ExpiresAt = expiresAt,
                UserId = user.Id,
                UserName = user.Name
            };
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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

    private string GenerateAdminJwtToken(AdminUser admin, out DateTime expiresAt)
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
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.UserName),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new Exception("Password is required.");
        }

        var salt = RandomNumberGenerator.GetBytes(PasswordSaltSize);
        using var deriveBytes = new Rfc2898DeriveBytes(
            password,
            salt,
            PasswordHashIterations,
            HashAlgorithmName.SHA256);

        var hash = deriveBytes.GetBytes(PasswordHashSize);

        return $"PBKDF2-SHA256${PasswordHashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var hashParts = storedHash.Split('$');
        if (hashParts.Length != 4 || hashParts[0] != "PBKDF2-SHA256")
        {
            return false;
        }

        if (!int.TryParse(hashParts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(hashParts[2]);
        var expectedHash = Convert.FromBase64String(hashParts[3]);

        using var deriveBytes = new Rfc2898DeriveBytes(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256);

        var actualHash = deriveBytes.GetBytes(expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string GenerateOtp()
    {
        return RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
    }

    private static string GenerateRefreshToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new Exception($"{fieldName} is required.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
