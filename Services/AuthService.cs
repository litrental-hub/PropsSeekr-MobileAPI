using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
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

    public async Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        var name = NormalizeRequired(request.Name, "Name");
        var mobileInput = NormalizeRequired(request.Mobile, "Mobile number");
        var mobile = NormalizeLocalPhoneNumber(mobileInput);
        var cognitoPhone = NormalizePhoneNumber(mobileInput);
        var email = NormalizeRequired(request.Email, "Email").ToLowerInvariant();
        var password = request.Password;
        var aadharNumber = NormalizeRequired(request.AadharNumber, "Aadhar number");
        var panCard = NormalizeRequired(request.PanCard, "PAN card").ToUpperInvariant();
        var gstNumber = NormalizeOptional(request.GstNumber)?.ToUpperInvariant();
        var reraRegistrationNumber = NormalizeOptional(request.ReraRegistrationNumber);

        await EnsureRegistrationIsUniqueAsync(mobile, email, aadharNumber, panCard);

        var useCognito = _configuration.GetValue<bool>("Cognito:UseCognito");
        var cognitoClientId = _configuration["Cognito:UserPoolClientId"];
        var cognitoUserPoolId = _configuration["Cognito:UserPoolId"];
        var cognitoClientSecret = _configuration["Cognito:ClientSecret"];

        if (useCognito)
        {
            if (string.IsNullOrWhiteSpace(cognitoClientId) || string.IsNullOrWhiteSpace(cognitoUserPoolId))
            {
                throw new InvalidOperationException("Cognito is enabled but UserPoolClientId/UserPoolId is not configured.");
            }

            try
            {
                using var cognito = CreateCognitoClient();

                var signUpRequest = new SignUpRequest
                {
                    ClientId = cognitoClientId,
                    Username = email, // using email as username
                    Password = password
                };

                signUpRequest.UserAttributes.Add(new AttributeType { Name = "email", Value = email });
                if (!string.IsNullOrWhiteSpace(cognitoPhone))
                {
                    signUpRequest.UserAttributes.Add(new AttributeType { Name = "phone_number", Value = cognitoPhone });
                }
                signUpRequest.UserAttributes.Add(new AttributeType { Name = "name", Value = name });

                if (!string.IsNullOrWhiteSpace(cognitoClientSecret))
                {
                    // Compute SECRET_HASH and add to payload
                    signUpRequest.SecretHash = ComputeSecretHash(email, cognitoClientId, cognitoClientSecret);
                }

                var signUpResponse = await cognito.SignUpAsync(signUpRequest);

                // Do not store passwords locally. Create a local profile record.
                await using var transaction = await _dbContext.Database.BeginTransactionAsync();

                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    MobileNumber = mobile,
                    Email = email,
                    PasswordHash = string.Empty,
                    AadharNumber = aadharNumber,
                    PanCard = panCard,
                    GSTNumber = gstNumber,
                    ReraRegistrationNumber = reraRegistrationNumber,
                    IsMobileVerified = false,
                    IsEmailVerified = signUpResponse.UserConfirmed,
                    Credits = 0,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow
                };

                _dbContext.Users.Add(user);

                try
                {
                    await _dbContext.SaveChangesAsync();
                    await CreateOtpAsync(mobile, "Registration successful. OTP verification is pending.");
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    await DeleteCognitoUser(email, cognitoClientId, cognitoClientSecret ?? string.Empty);
                    Console.WriteLine($"[Registration] local persistence failed: {ex.Message}");
                    throw new Exception("Registration failed while saving profile data. Please try again.");
                }

                // Trigger background email OTP like before
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
                    Message = "Registration successful. Please verify your email/phone as prompted by Cognito."
                };
            }
            catch (UsernameExistsException)
            {
                throw new Exception("A user with the same email already exists in Cognito.");
            }
            catch (Exception ex)
            {
                // Do not create a local user if Cognito registration fails
                throw new Exception($"Cognito registration failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Cognito customer authentication is required.");
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


    public async Task<LoginResponseDto> LoginAsync(LoginRequestDto request)
    {
        var identifier = NormalizeRequired(request.Identifier, "Mobile number or Email").ToLowerInvariant();
        var password = request.Password;

        var useCognito = _configuration.GetValue<bool>("Cognito:UseCognito");
        var cognitoClientId = _configuration["Cognito:UserPoolClientId"];
        var cognitoClientSecret = _configuration["Cognito:ClientSecret"];

        if (useCognito)
        {
            if (string.IsNullOrWhiteSpace(cognitoClientId))
            {
                throw new InvalidOperationException("Cognito login is enabled but UserPoolClientId is not configured.");
            }

            try
            {
                using var cognito = CreateCognitoClient();

                var authRequest = new InitiateAuthRequest
                {
                    AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
                    ClientId = cognitoClientId,
                    AuthParameters = new Dictionary<string, string>
                    {
                        { "USERNAME", identifier },
                        { "PASSWORD", password }
                    }
                };

                if (!string.IsNullOrWhiteSpace(cognitoClientSecret))
                {
                    authRequest.AuthParameters.Add("SECRET_HASH", ComputeSecretHash(identifier, cognitoClientId, cognitoClientSecret));
                }

                var authResponse = await cognito.InitiateAuthAsync(authRequest);

                var authResult = authResponse.AuthenticationResult;
                var accessToken = authResult?.AccessToken ?? string.Empty;
                var idToken = authResult?.IdToken ?? string.Empty;
                var refreshToken = authResult?.RefreshToken ?? string.Empty;
                var expiresIn = authResult?.ExpiresIn ?? 0;

                var user = await _dbContext.Users.FirstOrDefaultAsync(u =>
                    u.MobileNumber == identifier || (u.Email != null && u.Email.ToLower() == identifier));

                return new LoginResponseDto
                {
                    Success = true,
                    Message = "Login successful.",
                    Token = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : idToken,
                    RefreshToken = refreshToken,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                    User = user != null ? new AuthenticatedUserDto
                    {
                        Id = user.Id,
                        Name = user.Name,
                        MobileNumber = user.MobileNumber,
                        Email = user.Email,
                        IsMobileVerified = user.IsMobileVerified,
                        Credits = user.Credits
                    } : new AuthenticatedUserDto()
                };
            }
            catch (NotAuthorizedException)
            {
                throw new Exception("Invalid username or password.");
            }
            catch (UserNotFoundException)
            {
                throw new Exception("Invalid username or password.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Cognito login failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Cognito customer authentication is required.");
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
        var normalizedMobile = NormalizeLocalPhoneNumber(mobileNumber);

        if (!await _dbContext.Users.AnyAsync(x => x.MobileNumber == normalizedMobile))
        {
            throw new Exception("Mobile number is not registered.");
        }

        var now = DateTime.UtcNow;

        var activeOtps = await _dbContext.OtpVerifications
            .Where(x =>
                x.MobileNumber == normalizedMobile &&
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
            MobileNumber = NormalizeLocalPhoneNumber(mobileNumber),
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

    private AmazonCognitoIdentityProviderClient CreateCognitoClient()
    {
        var region = _configuration["Cognito:Region"] ?? _configuration["AWS:Region"] ?? "";
        return new AmazonCognitoIdentityProviderClient(RegionEndpoint.GetBySystemName(region));
    }

    private async Task DeleteCognitoUser(string username, string clientId, string clientSecret)
    {
        try
        {
            var userPoolId = _configuration["Cognito:UserPoolId"];
            if (string.IsNullOrWhiteSpace(userPoolId))
            {
                return;
            }

            using var cognito = CreateCognitoClient();
            var deleteRequest = new AdminDeleteUserRequest
            {
                UserPoolId = userPoolId,
                Username = username
            };

            await cognito.AdminDeleteUserAsync(deleteRequest);
        }
        catch
        {
            // best effort cleanup only; do not mask original registration failure
        }
    }

    private static string ComputeSecretHash(string username, string clientId, string clientSecret)
    {
        var dataString = username + clientId;
        var key = Encoding.UTF8.GetBytes(clientSecret);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataString));
        return Convert.ToBase64String(hash);
    }

    public async Task<VerifyOtpResponseDto> VerifyOtpAsync(VerifyOtpRequestDto request)
    {
        var mobileNumber = NormalizeRequired(request.Mobile, "Mobile number");
        var otpCode = NormalizeRequired(request.Otp, "OTP");
        var normalizedMobile = NormalizeLocalPhoneNumber(mobileNumber);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            var otp = await _dbContext.OtpVerifications
                .Where(o =>
                    o.MobileNumber == normalizedMobile &&
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

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.MobileNumber == normalizedMobile);

            if (user == null)
            {
                throw new Exception("User not found for the provided mobile number.");
            }

            user.IsMobileVerified = true;
            user.ModifiedDate = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return new VerifyOtpResponseDto
            {
                Token = string.Empty,
                ExpiresAt = DateTime.UtcNow,
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

    private static string NormalizePhoneNumber(string phone)
    {
        var digits = Regex.Replace(phone, "[^0-9]", string.Empty);
        if (digits.StartsWith("0"))
        {
            digits = digits.TrimStart('0');
        }

        if (digits.Length == 10)
        {
            return $"+91{digits}";
        }

        if (digits.StartsWith("91") && digits.Length == 12)
        {
            return $"+{digits}";
        }

        if (phone.StartsWith("+") && digits.Length >= 10)
        {
            return $"+{digits}";
        }

        throw new Exception("Invalid phone number format. Provide a valid 10-digit or E.164 phone number.");
    }

    private static string NormalizeLocalPhoneNumber(string phone)
    {
        var digits = Regex.Replace(phone, "[^0-9]", string.Empty);
        if (digits.StartsWith("91") && digits.Length == 12)
        {
            digits = digits.Substring(2);
        }

        if (digits.Length == 10)
        {
            return digits;
        }

        throw new Exception("Mobile number must be exactly 10 digits after removing country code.");
    }
}
