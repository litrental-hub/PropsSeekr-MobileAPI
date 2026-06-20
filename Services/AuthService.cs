using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;
using Microsoft.Extensions.Configuration;

namespace PropSeekr.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext dbContext,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<AuthService> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        if (await _dbContext.Users.AnyAsync(x =>
                x.MobileNumber == request.Mobile))
        {
            throw new Exception("Mobile number already registered.");
        }

        if (await _dbContext.Users.AnyAsync(x =>
                x.AadharNumber == request.AadharNumber))
        {
            throw new Exception("Aadhar number already registered.");
        }

        if (await _dbContext.Users.AnyAsync(x =>
                x.PanCard == request.PanCard))
        {
            throw new Exception("PAN card already registered.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            MobileNumber = request.Mobile,
            AadharNumber = request.AadharNumber,
            PanCard = request.PanCard,
            GSTNumber = request.GstNumber,
            ReraRegistrationNumber = request.ReraRegistrationNumber,
            IsMobileVerified = false,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);

        await _dbContext.SaveChangesAsync();

        return new RegisterResponseDto
        {
            UserId = user.Id,
            Message = "Registration successful."
        };
    }

    public async Task<AdminLoginResponseDto> AdminLoginAsync(AdminLoginRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new Exception("Invalid username or password.");
        }

        var userName = request.UserName.Trim();
        var admin = await _dbContext.AdminUsers
            .FirstOrDefaultAsync(x => x.UserName == userName && x.IsActive);

        if (admin == null || !VerifyPassword(request.Password, admin.PasswordHash))
        {
            throw new Exception("Invalid username or password.");
        }

        var token = GenerateJwtToken(
            admin.Id,
            admin.UserName,
            role: "Admin",
            mobileNumber: null,
            out var expiresAt);

        return new AdminLoginResponseDto
        {
            Token = token,
            ExpiresAt = expiresAt,
            AdminId = admin.Id,
            UserName = admin.UserName
        };
    }

    public async Task<OtpResponseDto> SendOtpAsync(SendOtpRequestDto request)
    {
        return await CreateOtpAsync(request.MobileNumber, invalidateExistingOtps: false, "OTP sent successfully.");
    }

    public async Task<OtpResponseDto> ResendOtpAsync(SendOtpRequestDto request)
    {
        return await CreateOtpAsync(request.MobileNumber, invalidateExistingOtps: true, "OTP resent successfully.");
    }

    public Task<LogoutResponseDto> LogoutAsync()
    {
        return Task.FromResult(new LogoutResponseDto
        {
            Message = "Logout successful. Token blacklist is not implemented; please discard the JWT on the client."
        });
    }

    private async Task<OtpResponseDto> CreateOtpAsync(
        string mobileNumber,
        bool invalidateExistingOtps,
        string message)
    {
        if (!await _dbContext.Users.AnyAsync(x => x.MobileNumber == mobileNumber))
        {
            throw new Exception("Mobile number is not registered.");
        }

        var now = DateTime.UtcNow;

        if (invalidateExistingOtps)
        {
            var activeOtps = await _dbContext.OtpVerifications
                .Where(x =>
                    x.MobileNumber == mobileNumber &&
                    !x.IsUsed)
                .ToListAsync();

            foreach (var activeOtp in activeOtps)
            {
                activeOtp.IsUsed = true;
            }
        }

        var otp = GenerateOtp();
        var expiresAt = now.AddMinutes(_configuration.GetValue<int>("Otp:ExpiryMinutes", 5));

        _dbContext.OtpVerifications.Add(new OtpVerification
        {
            Id = Guid.NewGuid(),
            MobileNumber = mobileNumber,
            OtpCode = otp,
            ExpiresAt = expiresAt,
            IsUsed = false,
            CreatedDate = now
        });

        await _dbContext.SaveChangesAsync();

        if (_environment.IsDevelopment() &&
            string.IsNullOrWhiteSpace(_configuration["Sms:Provider"]))
        {
            _logger.LogInformation("Development OTP for {MobileNumber}: {Otp}", mobileNumber, otp);
        }

        return new OtpResponseDto
        {
            Message = message,
            ExpiresAt = expiresAt
        };
    }

    public async Task<VerifyOtpResponseDto> VerifyOtpAsync(VerifyOtpRequestDto request)
    {
        var otp = await _dbContext.OtpVerifications
            .Where(o => o.MobileNumber == request.Mobile && !o.IsUsed)
            .OrderByDescending(o => o.CreatedDate)
            .FirstOrDefaultAsync();

        if (otp == null || otp.OtpCode != request.Otp || otp.ExpiresAt < DateTime.UtcNow)
        {
            throw new Exception("Invalid or expired OTP.");
        }

        otp.IsUsed = true;
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.MobileNumber == request.Mobile);

        if (user == null)
        {
            throw new Exception("User not found for the provided mobile number.");
        }

        user.IsMobileVerified = true;
        user.ModifiedDate = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        var token = GenerateJwtToken(user, out var expiresAt);

        return new VerifyOtpResponseDto
        {
            Token = token,
            ExpiresAt = expiresAt,
            UserId = user.Id,
            UserName = user.Name
        };
    }

    private string GenerateJwtToken(User user, out DateTime expiresAt)
    {
        return GenerateJwtToken(
            user.Id,
            user.Name,
            role: "User",
            mobileNumber: user.MobileNumber,
            out expiresAt);
    }

    private string GenerateJwtToken(
        Guid subjectId,
        string userName,
        string role,
        string? mobileNumber,
        out DateTime expiresAt)
    {
        var key = _configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        var expiresMinutes = _configuration.GetValue<int>("Jwt:ExpiresMinutes", 60);
        expiresAt = DateTime.UtcNow.AddMinutes(expiresMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subjectId.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, userName),
            new(ClaimTypes.Role, role),
            new("role", role)
        };

        if (!string.IsNullOrWhiteSpace(mobileNumber))
        {
            claims.Add(new Claim(ClaimTypes.MobilePhone, mobileNumber));
            claims.Add(new Claim("mobile", mobileNumber));
        }

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var hashParts = storedHash.Split('$');
        if (hashParts.Length != 4 ||
            hashParts[0] != "PBKDF2-SHA256" ||
            !int.TryParse(hashParts[1], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(hashParts[2]);
            expectedHash = Convert.FromBase64String(hashParts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

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
}
