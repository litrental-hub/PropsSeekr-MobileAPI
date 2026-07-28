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
    private const int PasswordHashIterations = 100000;
    private const int PasswordSaltSize = 16;
    private const int PasswordHashSize = 32;

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
        var name = NormalizeRequired(request.Name, "Name");
        var mobile = NormalizeRequired(request.Mobile, "Mobile number");
        var email = NormalizeRequired(request.Email, "Email").ToLowerInvariant();
        var password = request.Password;
        var addressLine1 = NormalizeRequired(request.AddressLine1, "Address line 1");
        var addressLine2 = NormalizeOptional(request.AddressLine2);
        var city = NormalizeRequired(request.City, "City");
        var state = NormalizeRequired(request.State, "State");
        var pincode = NormalizeRequired(request.Pincode, "Pincode");
        var aadharNumber = NormalizeRequired(request.AadharNumber, "Aadhar number");
        var panCard = NormalizeRequired(request.PanCard, "PAN card").ToUpperInvariant();
        var gstNumber = NormalizeOptional(request.GstNumber)?.ToUpperInvariant();
        var reraRegistrationNumber = NormalizeOptional(request.ReraRegistrationNumber);

        await EnsureRegistrationIsUniqueAsync(mobile, email, aadharNumber, panCard);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = name,
            MobileNumber = mobile,
            Email = email,
            PasswordHash = HashPassword(password),
            AddressLine1 = addressLine1,
            AddressLine2 = addressLine2,
            City = city,
            State = state,
            Pincode = pincode,
            AadharNumber = aadharNumber,
            PanCard = panCard,
            GSTNumber = gstNumber,
            ReraRegistrationNumber = reraRegistrationNumber,
            RemainingCreditBalance = 0,
            IsEmailVerified = false,
            IsMobileVerified = false,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        await CreateOtpAsync(mobile, "Registration successful. OTP verification is pending.");

        await transaction.CommitAsync();

        return new RegisterResponseDto
        {
            UserId = user.Id,
            Message = "Registration successful. OTP verification is pending."
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
            Message = "Logout successful. Token blacklist is not implemented; please discard the JWT on the client."
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

            var claimed = await _dbContext.OtpVerifications
                .Where(o => o.Id == otp.Id && !o.IsUsed)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(o => o.IsUsed, true));

            if (claimed == 0)
            {
                throw new Exception("Invalid or expired OTP.");
            }

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
