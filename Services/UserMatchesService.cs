using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class UserMatchesService : IUserMatchesService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserMatchesService> _logger;

    public UserMatchesService(
        AppDbContext dbContext,
        ILogger<UserMatchesService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<UserMatchesResponseDto> GetUserMatchesAsync(Guid userId)
    {
        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (user == null)
        {
            throw new KeyNotFoundException("User not found.");
        }

        // 1. Fetch user's own property requests to use as search criteria
        var userRequests = await _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync();

        // 2. Query property requests posted by OTHER users (p.UserId != userId)
        var otherPropertyRequests = await _dbContext.PropertyRequests
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.UserId != userId)
            .OrderByDescending(p => p.PostedAt)
            .Take(100)
            .ToListAsync();

        // 3. Fetch set of property IDs unlocked by this user
        var unlockedPropertyIds = (await _dbContext.UnlockedProperties
            .AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.PropertyRequestId)
            .ToListAsync())
            .ToHashSet();

        // 4. Calculate matches and mask sensitive contact data unless unlocked
        var items = new List<UserMatchItemDto>();

        foreach (var propertyRequest in otherPropertyRequests)
        {
            var isUnlocked = unlockedPropertyIds.Contains(propertyRequest.Id);
            var matchScore = CalculateMatchScore(propertyRequest, userRequests);

            // Only include relevant matches (score >= 50)
            if (matchScore >= 50)
            {
                items.Add(new UserMatchItemDto
                {
                    Id = propertyRequest.Id.ToString(),
                    Title = propertyRequest.Title,
                    TransactionType = propertyRequest.TransactionType,
                    Category = propertyRequest.Category,
                    City = propertyRequest.City,
                    Locality = propertyRequest.Locality,
                    BudgetMin = propertyRequest.BudgetMin,
                    BudgetMax = propertyRequest.BudgetMax,
                    PostedAt = propertyRequest.PostedAt,
                    PostedTimeAgo = GetTimeAgoText(propertyRequest.PostedAt),
                    MatchScore = matchScore,
                    IsUnlocked = isUnlocked,
                    // Security Enforcement: Contact details are NULL unless explicitly unlocked
                    OwnerContact = isUnlocked ? new ContactDetailsDto
                    {
                        OwnerName = propertyRequest.User?.Name ?? "Property Owner",
                        OwnerMobile = propertyRequest.User?.MobileNumber ?? "N/A",
                        OwnerEmail = propertyRequest.User?.Email
                    } : null
                });
            }
        }

        // Sort by highest match score
        items = items.OrderByDescending(i => i.MatchScore).ThenByDescending(i => i.PostedAt).ToList();

        return new UserMatchesResponseDto
        {
            Success = true,
            TotalCount = items.Count,
            Data = items
        };
    }

    public async Task<UnlockPropertyResponseDto> UnlockPropertyAsync(Guid userId, UnlockPropertyRequestDto request)
    {
        var targetProperty = await _dbContext.PropertyRequests
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == request.PropertyRequestId);

        if (targetProperty == null)
        {
            throw new KeyNotFoundException("Property request not found.");
        }

        var user = await _dbContext.Users.FindAsync(userId);
        if (user == null)
        {
            throw new KeyNotFoundException("User not found.");
        }

        // Check 1: User unlocking their own property (no token required)
        if (targetProperty.UserId == userId)
        {
            return new UnlockPropertyResponseDto
            {
                Success = true,
                Message = "You own this property listing.",
                CreditsRemaining = user.Credits,
                UnlockedContact = new ContactDetailsDto
                {
                    OwnerName = user.Name,
                    OwnerMobile = user.MobileNumber,
                    OwnerEmail = user.Email
                }
            };
        }

        // Check 2: Already unlocked check (idempotency)
        var existingUnlock = await _dbContext.UnlockedProperties
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId && u.PropertyRequestId == request.PropertyRequestId);

        if (existingUnlock != null)
        {
            return new UnlockPropertyResponseDto
            {
                Success = true,
                Message = "Property details are already unlocked.",
                CreditsRemaining = user.Credits,
                UnlockedContact = new ContactDetailsDto
                {
                    OwnerName = targetProperty.User?.Name ?? "Property Owner",
                    OwnerMobile = targetProperty.User?.MobileNumber ?? "N/A",
                    OwnerEmail = targetProperty.User?.Email
                }
            };
        }

        // Check 3: Sufficient Credit Check (Requires 1 Token / Credit)
        if (user.Credits < 1)
        {
            _logger.LogWarning("User {UserId} attempted to unlock property {PropertyId} but has insufficient credits ({Credits}).", userId, request.PropertyRequestId, user.Credits);
            return new UnlockPropertyResponseDto
            {
                Success = false,
                Message = "Insufficient credits. Please purchase a credit package (1 Token = ₹300) to unlock contact details.",
                CreditsRemaining = user.Credits,
                UnlockedContact = null
            };
        }

        // Process Unlock: Deduct 1 Credit & Record Unlock
        user.Credits -= 1;
        user.ModifiedDate = DateTime.UtcNow;

        var unlockRecord = new UnlockedProperty
        {
            UserId = userId,
            PropertyRequestId = request.PropertyRequestId,
            UnlockedAt = DateTime.UtcNow
        };

        _dbContext.UnlockedProperties.Add(unlockRecord);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("User {UserId} successfully unlocked property {PropertyId}. 1 Token deducted. Remaining Credits: {Credits}", userId, request.PropertyRequestId, user.Credits);

        return new UnlockPropertyResponseDto
        {
            Success = true,
            Message = "Property contact details unlocked successfully!",
            CreditsRemaining = user.Credits,
            UnlockedContact = new ContactDetailsDto
            {
                OwnerName = targetProperty.User?.Name ?? "Property Owner",
                OwnerMobile = targetProperty.User?.MobileNumber ?? "N/A",
                OwnerEmail = targetProperty.User?.Email
            }
        };
    }

    public async Task<UserMatchesResponseDto> GetUnlockedPropertiesAsync(Guid userId)
    {
        var unlockedRecords = await _dbContext.UnlockedProperties
            .AsNoTracking()
            .Include(u => u.PropertyRequest)
            .ThenInclude(p => p!.User)
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.UnlockedAt)
            .ToListAsync();

        var items = new List<UserMatchItemDto>();

        foreach (var record in unlockedRecords)
        {
            if (record.PropertyRequest != null)
            {
                items.Add(new UserMatchItemDto
                {
                    Id = record.PropertyRequest.Id.ToString(),
                    Title = record.PropertyRequest.Title,
                    TransactionType = record.PropertyRequest.TransactionType,
                    Category = record.PropertyRequest.Category,
                    City = record.PropertyRequest.City,
                    Locality = record.PropertyRequest.Locality,
                    BudgetMin = record.PropertyRequest.BudgetMin,
                    BudgetMax = record.PropertyRequest.BudgetMax,
                    PostedAt = record.PropertyRequest.PostedAt,
                    PostedTimeAgo = GetTimeAgoText(record.PropertyRequest.PostedAt),
                    MatchScore = 100,
                    IsUnlocked = true,
                    OwnerContact = new ContactDetailsDto
                    {
                        OwnerName = record.PropertyRequest.User?.Name ?? "Property Owner",
                        OwnerMobile = record.PropertyRequest.User?.MobileNumber ?? "N/A",
                        OwnerEmail = record.PropertyRequest.User?.Email
                    }
                });
            }
        }

        return new UserMatchesResponseDto
        {
            Success = true,
            TotalCount = items.Count,
            Data = items
        };
    }

    private int CalculateMatchScore(PropertyRequest target, List<PropertyRequest> userRequests)
    {
        // Default base score for properties in the area
        var baseScore = 60;

        if (userRequests.Count == 0)
        {
            return baseScore;
        }

        var maxScore = baseScore;

        foreach (var req in userRequests)
        {
            var score = 50;

            if (string.Equals(req.Category, target.Category, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            if (string.Equals(req.TransactionType, target.TransactionType, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            if (string.Equals(req.City, target.City, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (string.Equals(req.Locality, target.Locality, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            // Budget overlap check
            if (req.BudgetMin.HasValue && target.BudgetMax.HasValue && req.BudgetMin <= target.BudgetMax)
            {
                score += 5;
            }

            if (score > maxScore)
            {
                maxScore = score;
            }
        }

        return Math.Min(maxScore, 100);
    }

    private string GetTimeAgoText(DateTime postedAt)
    {
        var timeSpan = DateTime.UtcNow - postedAt;

        if (timeSpan.TotalMinutes < 1)
            return "Just now";

        if (timeSpan.TotalHours < 1)
            return $"{(int)timeSpan.TotalMinutes}m ago";

        if (timeSpan.TotalDays < 1)
            return $"{(int)timeSpan.TotalHours}h ago";

        if (timeSpan.TotalDays == 1)
            return "Yesterday";

        if (timeSpan.TotalDays < 7)
            return $"{(int)timeSpan.TotalDays}d ago";

        return postedAt.ToString("dd MMM");
    }
}
