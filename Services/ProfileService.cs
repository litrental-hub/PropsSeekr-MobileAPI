using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Profile;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class ProfileService : IProfileService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public ProfileService(
        AppDbContext dbContext,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<ProfileResponseDto> GetProfileAsync(Guid userId)
    {
        var user = await GetUserAsync(userId);
        await EnsureLegacyBrokerLinkedAsync(user);

        return MapToResponse(user);
    }

    public async Task<ProfileResponseDto> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request)
    {
        var user = await GetUserAsync(userId);
        await EnsureLegacyBrokerLinkedAsync(user);
        var normalizedEmail = NormalizeOptionalEmail(request.Email);
        var normalizedProfilePhotoUrl = NormalizeOptional(request.ProfilePhotoUrl);

        ValidateProfilePhotoUrl(normalizedProfilePhotoUrl);

        if (normalizedEmail != null)
        {
            var emailInUse = await _dbContext.Users.AnyAsync(x => x.Id != userId && x.Email != null && x.Email.ToLower() == normalizedEmail);
            if (emailInUse)
            {
                throw new Exception("Email already registered.");
            }
        }

        if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            user.IsEmailVerified = false;
        }

        user.Name = request.Name.Trim();
        user.Email = normalizedEmail;
        user.ProfilePhotoUrl = normalizedProfilePhotoUrl;
        user.ModifiedDate = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return MapToResponse(user);
    }

    public async Task<ProfileResponseDto> UploadPhotoAsync(Guid userId, IFormFile file)
    {
        ValidatePhoto(file);

        var user = await GetUserAsync(userId);
        await EnsureLegacyBrokerLinkedAsync(user);
        var extension = Path.GetExtension(file.FileName);
        var fileName = $"{userId:N}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var relativeFolder = _configuration["Uploads:ProfilePhotoFolder"] ?? "uploads/profile-photos";
        var webRootPath = _environment.WebRootPath;

        if (string.IsNullOrWhiteSpace(webRootPath))
        {
            webRootPath = Path.Combine(_environment.ContentRootPath, "wwwroot");
        }

        var uploadDirectory = Path.Combine(webRootPath, relativeFolder);
        Directory.CreateDirectory(uploadDirectory);

        var filePath = Path.Combine(uploadDirectory, fileName);
        await using (var stream = new FileStream(filePath, FileMode.CreateNew))
        {
            await file.CopyToAsync(stream);
        }

        user.ProfilePhotoUrl = $"/{relativeFolder.Replace("\\", "/").Trim('/')}/{fileName}";
        user.ModifiedDate = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        return MapToResponse(user);
    }

    private async Task<User> GetUserAsync(Guid userId)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);

        if (user == null)
        {
            throw new Exception("User not found.");
        }

        return user;
    }

    private void ValidatePhoto(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            throw new Exception("Profile photo is required.");
        }

        var maxFileSizeInBytes = _configuration.GetValue<long>("Uploads:MaxProfilePhotoSizeInBytes", 5 * 1024 * 1024);
        if (file.Length > maxFileSizeInBytes)
        {
            throw new Exception("Profile photo size exceeds the allowed limit.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
        {
            throw new Exception("Only JPG, PNG and WEBP profile photos are allowed.");
        }

        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            throw new Exception("Invalid profile photo content type.");
        }
    }

    private static ProfileResponseDto MapToResponse(User user)
    {
        return new ProfileResponseDto
        {
            Id = user.Id,
            Name = user.Name,
            MobileNumber = user.MobileNumber,
            Email = user.Email,
            ProfilePhotoUrl = user.ProfilePhotoUrl,
            RemainingCreditBalance = user.Credits,
            IsEmailVerified = user.IsEmailVerified,
            IsMobileVerified = user.IsMobileVerified,
            BrokerId = user.BrokerId
        };
    }

    private static void ValidateProfilePhotoUrl(string? value)
    {
        if (value == null)
        {
            return;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return;
        }

        if (Uri.TryCreate(value, UriKind.Relative, out _) && value.StartsWith('/'))
        {
            return;
        }

        throw new Exception("Profile photo URL must be an absolute HTTP(S) URL or an app-relative path.");
    }

    private static string? NormalizeOptionalEmail(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized?.ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task<int> EnsureLegacyBrokerLinkedAsync(User user)
    {
        if (user.BrokerId.HasValue)
        {
            // Sync credits from wallet to user record to avoid getting out of sync
            var existingWallet = await _dbContext.CreditWallets.AsNoTracking().FirstOrDefaultAsync(w => w.BrokerId == user.BrokerId.Value);
            if (existingWallet != null)
            {
                var expectedCredits = existingWallet.FreeCreditsBalance + existingWallet.PaidCreditsBalance;
                if (user.Credits != expectedCredits)
                {
                    user.Credits = expectedCredits;
                    user.ModifiedDate = DateTime.UtcNow;
                    _dbContext.Users.Update(user);
                    await _dbContext.SaveChangesAsync();
                }
            }
            return user.BrokerId.Value;
        }

        var mobileRaw = user.MobileNumber ?? string.Empty;
        var digitsOnly = new string(mobileRaw.Where(char.IsDigit).ToArray());
        var normalizedMobile = digitsOnly.Length >= 10 ? digitsOnly.Substring(digitsOnly.Length - 10) : digitsOnly;

        var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => 
            b.PhoneNumber != null && 
            (b.PhoneNumber == user.MobileNumber || b.PhoneNumber.EndsWith(normalizedMobile)));
        if (broker == null)
        {
            broker = new Broker
            {
                Name = user.Name,
                PhoneNumber = user.MobileNumber ?? "",
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            };
            _dbContext.Brokers.Add(broker);
            await _dbContext.SaveChangesAsync();
        }

        var wallet = await _dbContext.CreditWallets.FirstOrDefaultAsync(w => w.BrokerId == broker.Id);
        if (wallet == null)
        {
            wallet = new CreditWallet
            {
                BrokerId = broker.Id,
                FreeCreditsBalance = 10,
                PaidCreditsBalance = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.CreditWallets.Add(wallet);
            await _dbContext.SaveChangesAsync();

            var grantTx = new CreditTransaction
            {
                BrokerId = broker.Id,
                Type = "grant",
                Amount = 10,
                BalanceAfter = 10,
                ReferenceType = "monthly_grant",
                Notes = "Signup free grant credits balance",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.CreditTransactions.Add(grantTx);
            await _dbContext.SaveChangesAsync();
        }

        user.BrokerId = broker.Id;
        user.Credits = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance;
        user.ModifiedDate = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return broker.Id;
    }
}

