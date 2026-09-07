using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using System.Net.Sockets;

namespace PropSeekr.Services;

public class AuthService : IAuthService
{
    private const int PasswordSaltSize = 16;
    private const int PasswordHashSize = 32;
    private const int PasswordHashIterations = 100000;

    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IOtpDeliveryService _otpDeliveryService;
    private readonly IEmailOtpService _emailOtpService;
    private readonly IBrokerIdentityService _brokerIdentityService;
    private readonly IHostEnvironment _hostEnvironment;

    public AuthService(
        AppDbContext dbContext,
        IConfiguration configuration,
        IOtpDeliveryService otpDeliveryService,
        IEmailOtpService emailOtpService,
        IBrokerIdentityService brokerIdentityService,
        IHostEnvironment hostEnvironment)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _otpDeliveryService = otpDeliveryService;
        _emailOtpService = emailOtpService;
        _brokerIdentityService = brokerIdentityService;
        _hostEnvironment = hostEnvironment;
    }

    public async Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        var name = NormalizeRequired(request.Name, "Name");
        var mobile = NormalizeMobileNumber(request.Mobile);
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

        var passwordHash = HashPassword(password);
        var bypassVerification = IsLocalRegistrationVerificationBypassed();
        if (bypassVerification)
        {
            await EnsureRegistrationIsUniqueAsync(mobile, email, aadharNumber, panCard);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            var user = CreateUser(
                name, mobile, email, passwordHash, addressLine1, addressLine2, city, state,
                pincode, aadharNumber, panCard, gstNumber, reraRegistrationNumber);
            user.IsMobileVerified = true;
            user.IsEmailVerified = true;
            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();
            await _brokerIdentityService.GetOrCreateBrokerIdAsync(user.Id);
            await transaction.CommitAsync();

            return new RegisterResponseDto
            {
                UserId = user.Id,
                VerificationRequired = false,
                Message = "Registration successful. Local verification was bypassed."
            };
        }

        await EnsureRegistrationIsUniqueAsync(mobile, email, aadharNumber, panCard);
        var now = DateTime.UtcNow;
        var existingPending = await _dbContext.PendingRegistrations
            .Where(x => x.Email == email || x.MobileNumber == mobile ||
                        x.AadharNumber == aadharNumber || x.PanCard == panCard)
            .ToListAsync();
        _dbContext.PendingRegistrations.RemoveRange(existingPending);
        if (existingPending.Count > 0)
            await _dbContext.SaveChangesAsync();

        var pending = new PendingRegistration
        {
            Name = name,
            MobileNumber = mobile,
            Email = email,
            PasswordHash = passwordHash,
            AddressLine1 = addressLine1,
            AddressLine2 = addressLine2,
            City = city,
            State = state,
            Pincode = pincode,
            AadharNumber = aadharNumber,
            PanCard = panCard,
            GstNumber = gstNumber,
            ReraRegistrationNumber = reraRegistrationNumber,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        _dbContext.PendingRegistrations.Add(pending);
        await _dbContext.SaveChangesAsync();
        await _emailOtpService.SendEmailOtpAsync(new SendEmailOtpRequestDto
        {
            Email = email,
            Purpose = "EmailVerification"
        }, clientIp: null);

        return new RegisterResponseDto
        {
            PendingRegistrationId = pending.Id,
            VerificationRequired = true,
            VerificationChannel = "email",
            Message = "Verify your email, then mobile number, to create your account."
        };
    }

    public async Task<LoginResponseDto> LoginAsync(LoginRequestDto request)
    {
        var identifier = NormalizeRequired(request.Identifier, "Username, mobile number, or email").ToLowerInvariant();
        var password = request.Password;

        // Every identity lives in Users. Roles determine authorization after
        // a successful login; they do not select a different login workflow.
        var user = await FindActiveUserWithRetryAsync(identifier);

        if (user == null || !VerifyPassword(password, user.PasswordHash))
        {
            throw new Exception("Invalid username, mobile number, email, or password.");
        }

        var role = NormalizeRole(user.Role);
        if (role != "Admin" && !IsLocalRegistrationVerificationBypassed() && !user.IsEmailVerified)
        {
            throw new Exception("Email verification is required before login.");
        }
        if (role != "Admin")
        {
            if (!user.IsMobileVerified) throw new Exception("Mobile verification is required before login.");
            await _brokerIdentityService.GetOrCreateBrokerIdAsync(user.Id);
        }

        var token = GenerateJwtToken(user, out var expiresAt, role);
        var refreshToken = GenerateRefreshToken();

        return new LoginResponseDto
        {
            Success = true,
            Message = role == "Admin" ? "Admin login successful." : "Login successful.",
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            Role = role,
            User = new AuthenticatedUserDto
            {
                Id = user.Id,
                BrokerId = user.BrokerId,
                Name = user.Name,
                MobileNumber = user.MobileNumber ?? string.Empty,
                Email = user.Email,
                IsMobileVerified = user.IsMobileVerified,
                IsEmailVerified = user.IsEmailVerified,
                Role = role
            }
        };
    }

    private bool IsLocalRegistrationVerificationBypassed() =>
        _hostEnvironment.IsDevelopment() &&
        _configuration.GetValue<bool>("Auth:BypassRegistrationVerification");

    private async Task<User?> FindActiveUserWithRetryAsync(string identifier)
    {
        const int maximumAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _dbContext.Users.FirstOrDefaultAsync(u =>
                    u.IsActive &&
                    ((u.UserName != null && u.UserName.ToLower() == identifier) ||
                     (u.MobileNumber != null && u.MobileNumber == identifier) ||
                     (u.Email != null && u.Email.ToLower() == identifier)));
            }
            catch (Exception ex) when (attempt < maximumAttempts && IsDatabaseConnectivityFailure(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    private static bool IsDatabaseConnectivityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or SocketException or TimeoutException)
                return true;
        }

        return false;
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

        var now = DateTime.UtcNow;
        if (await _dbContext.PendingRegistrations.AnyAsync(x => x.ExpiresAt >= now &&
            (x.MobileNumber == mobileNumber || x.Email == email || x.AadharNumber == aadharNumber || x.PanCard == panCard)))
        {
            throw new Exception("A registration for these details is already awaiting verification. Complete it or register again after it expires.");
        }
    }

    private async Task<OtpResponseDto> CreateOtpAsync(
        string mobileNumber,
        string message)
    {
        var hasUser = await _dbContext.Users.AnyAsync(x => x.MobileNumber == mobileNumber);
        var pending = await _dbContext.PendingRegistrations
            .SingleOrDefaultAsync(x => x.MobileNumber == mobileNumber && x.ExpiresAt >= DateTime.UtcNow);
        if (!hasUser && pending is null)
        {
            throw new Exception("No active registration was found for this mobile number.");
        }
        if (!hasUser && !await HasVerifiedRegistrationEmailAsync(pending!.Email, pending.CreatedAt))
        {
            throw new Exception("Email verification is required before sending the mobile OTP.");
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
        var mobileNumber = NormalizeMobileNumber(request.Mobile);
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
            if (user is null)
            {
                var pending = await _dbContext.PendingRegistrations
                    .SingleOrDefaultAsync(x => x.MobileNumber == mobileNumber && x.ExpiresAt >= DateTime.UtcNow);
                if (pending is null || !await HasVerifiedRegistrationEmailAsync(pending.Email, pending.CreatedAt))
                    throw new Exception("Email verification is required before creating the account.");

                await EnsureFinalRegistrationIsUniqueAsync(pending);
                user = CreateUser(
                    pending.Name, pending.MobileNumber, pending.Email, pending.PasswordHash,
                    pending.AddressLine1, pending.AddressLine2, pending.City, pending.State,
                    pending.Pincode, pending.AadharNumber, pending.PanCard, pending.GstNumber,
                    pending.ReraRegistrationNumber);
                user.IsMobileVerified = true;
                user.IsEmailVerified = true;
                _dbContext.Users.Add(user);
                _dbContext.PendingRegistrations.Remove(pending);
            }
            else
            {
                user.IsMobileVerified = true;
                user.ModifiedDate = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync();
            await _brokerIdentityService.GetOrCreateBrokerIdAsync(user.Id);
            await transaction.CommitAsync();

            var token = GenerateJwtToken(user, out var expiresAt);

            return new VerifyOtpResponseDto
            {
                Token = token,
                ExpiresAt = expiresAt,
                UserId = user.Id,
                BrokerId = user.BrokerId,
                UserName = user.Name
            };
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<bool> HasVerifiedRegistrationEmailAsync(string email, DateTime registrationCreatedAt) =>
        await _dbContext.EmailOtpRecords.AnyAsync(record =>
            record.Email == email &&
            record.Purpose == "EmailVerification" &&
            record.IsUsed &&
            record.UsedAt != null &&
            record.UsedAt >= registrationCreatedAt);

    private async Task EnsureFinalRegistrationIsUniqueAsync(PendingRegistration pending)
    {
        if (await _dbContext.Users.AnyAsync(user =>
                user.MobileNumber == pending.MobileNumber ||
                (user.Email != null && user.Email.ToLower() == pending.Email) ||
                user.AadharNumber == pending.AadharNumber ||
                user.PanCard == pending.PanCard))
        {
            throw new Exception("An account was created with these registration details. Please sign in instead.");
        }
    }

    private static User CreateUser(
        string name,
        string mobile,
        string email,
        string passwordHash,
        string addressLine1,
        string? addressLine2,
        string city,
        string state,
        string pincode,
        string aadharNumber,
        string panCard,
        string? gstNumber,
        string? reraRegistrationNumber) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            MobileNumber = mobile,
            Email = email,
            PasswordHash = passwordHash,
            AddressLine1 = addressLine1,
            AddressLine2 = addressLine2,
            City = city,
            State = state,
            Pincode = pincode,
            AadharNumber = aadharNumber,
            PanCard = panCard,
            GSTNumber = gstNumber,
            ReraRegistrationNumber = reraRegistrationNumber,
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

    private string GenerateJwtToken(User user, out DateTime expiresAt, string? role = null)
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
            new Claim(ClaimTypes.MobilePhone, user.MobileNumber ?? string.Empty),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim(ClaimTypes.Role, NormalizeRole(role ?? user.Role))
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

    private static string NormalizeRole(string? role) =>
        string.Equals(role?.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "User";

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

    private static string NormalizeMobileNumber(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 10) throw new Exception("Mobile number must contain 10 digits.");
        return digits[^10..];
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
