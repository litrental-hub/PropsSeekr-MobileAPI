using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;
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

    public async Task<UserMatchesResponseDto> GetUserMatchesAsync(Guid userId, string? transactionType = null)
    {
        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (user == null)
        {
            throw new KeyNotFoundException("User not found.");
        }

        // Standardize the target transaction type string
        string? targetTxType = null;
        if (!string.IsNullOrWhiteSpace(transactionType))
        {
            if (transactionType.Contains("rent", StringComparison.OrdinalIgnoreCase) || 
                transactionType.Contains("lease", StringComparison.OrdinalIgnoreCase))
            {
                targetTxType = "RENTAL";
            }
            else if (transactionType.Contains("sale", StringComparison.OrdinalIgnoreCase) || 
                     transactionType.Contains("buy", StringComparison.OrdinalIgnoreCase) || 
                     transactionType.Contains("sell", StringComparison.OrdinalIgnoreCase))
            {
                targetTxType = "BUY_SELL";
            }
        }

        // Admin bypass logic: if email is admin@gmail.com or propseekr@gmail.com, calculate and show matches across all listings
        var isAdmin = string.Equals(user.Email, "admin@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(user.Email, "propseekr@gmail.com", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            var adminItems = new List<UserMatchItemDto>();

            try
            {
                var conn = _dbContext.Database.GetDbConnection();
                var wasOpen = conn.State == System.Data.ConnectionState.Open;
                if (!wasOpen) await conn.OpenAsync();

                var sqlFilter = "";
                if (targetTxType == "RENTAL")
                {
                    sqlFilter = "AND (l.listing_type = 'RENT' OR l.listing_type = 'LEASE')";
                }
                else if (targetTxType == "BUY_SELL")
                {
                    sqlFilter = "AND l.listing_type = 'SELL'";
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT 
                        m.matchid,
                        m.match_score,
                        l.listingid,
                        l.raw_message_text AS l_text,
                        l.listing_type AS l_type,
                        l.property_type AS l_prop_type,
                        l.configuration AS l_config,
                        l.price AS l_price,
                        l.price_unit AS l_price_unit,
                        l.furnishing AS l_furnishing,
                        ml.area AS l_area,
                        
                        r.requirementid,
                        r.raw_message_text AS r_text,
                        r.requirement_type AS r_type,
                        r.property_type AS r_prop_type,
                        r.configurations AS r_configs,
                        r.budget AS r_budget,
                        r.budget_unit AS r_budget_unit,
                        r.furnishing_pref AS r_furnishing,
                        mr.area AS r_area,
                        
                        bl.name AS l_broker_name,
                        bl.phone_number AS l_broker_mobile,
                        br.name AS r_broker_name,
                        br.phone_number AS r_broker_mobile,
                        m.created_at
                    FROM public.matches m
                    JOIN public.listings l ON l.listingid = m.listing_id
                    JOIN public.requirements r ON r.requirementid = m.requirement_id
                    LEFT JOIN public.master ml ON ml.masterid = l.master_id
                    LEFT JOIN public.master mr ON mr.masterid = r.preferred_locality_ids[1]
                    LEFT JOIN public.brokers bl ON bl.brokerid = l.broker_id
                    LEFT JOIN public.brokers br ON br.brokerid = r.broker_id
                    WHERE m.status = 'MATCHED'
                    {sqlFilter}
                    ORDER BY m.match_score DESC, m.created_at DESC;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var matchScoreVal = reader["match_score"];
                    var matchScore = matchScoreVal != DBNull.Value ? Convert.ToInt32(Convert.ToDecimal(matchScoreVal)) : 100;

                    var lText = reader["l_text"] as string ?? string.Empty;
                    var lType = reader["l_type"] as string ?? "SELL";
                    var lPropType = reader["l_prop_type"] as string ?? "Flat";
                    var lConfig = reader["l_config"] as string ?? "2BHK";
                    var lPriceVal = reader["l_price"];
                    long? lPrice = (lPriceVal != DBNull.Value && lPriceVal != null) ? Convert.ToInt64(Convert.ToDecimal(lPriceVal)) : null;
                    var lFurnishing = reader["l_furnishing"] as string ?? "Semi-Furnished";
                    var lArea = reader["l_area"] as string ?? "Indore";

                    var rText = reader["r_text"] as string ?? string.Empty;
                    var rType = reader["r_type"] as string ?? "BUY";
                    var rPropType = reader["r_prop_type"] as string ?? "Flat";
                    var rBudgetVal = reader["r_budget"];
                    long? rBudget = (rBudgetVal != DBNull.Value && rBudgetVal != null) ? Convert.ToInt64(Convert.ToDecimal(rBudgetVal)) : null;
                    var rArea = reader["r_area"] as string ?? "Indore";

                    var lBrokerName = reader["l_broker_name"] as string ?? "Listing Broker";
                    var lBrokerMobile = reader["l_broker_mobile"] as string ?? "N/A";

                    var postedAt = reader["created_at"] is DateTime dt ? dt : DateTime.UtcNow;

                    var lCategory = (string.Equals(lPropType, "plot", StringComparison.OrdinalIgnoreCase) || 
                                     string.Equals(lPropType, "land", StringComparison.OrdinalIgnoreCase)) ? "Plot/Land" : "Residential";
                    var rCategory = (string.Equals(rPropType, "plot", StringComparison.OrdinalIgnoreCase) || 
                                     string.Equals(rPropType, "land", StringComparison.OrdinalIgnoreCase)) ? "Plot/Land" : "Residential";

                    var mappedTxType = (string.Equals(lType, "RENT", StringComparison.OrdinalIgnoreCase) || 
                                        string.Equals(lType, "LEASE", StringComparison.OrdinalIgnoreCase))
                                        ? "RENTAL" : "BUY_SELL";

                    var dummyProperty = new PropertyRequest
                    {
                        Title = string.IsNullOrWhiteSpace(lText) ? $"{lConfig} {lPropType} in {lArea}" : lText,
                        TransactionType = mappedTxType,
                        Category = lCategory,
                        Locality = lArea,
                        BudgetMin = lPrice
                    };

                    var dummyRequirement = new PropertyRequest
                    {
                        Title = string.IsNullOrWhiteSpace(rText) ? $"{rPropType} in {rArea}" : rText,
                        TransactionType = (string.Equals(rType, "RENT", StringComparison.OrdinalIgnoreCase) || 
                                           string.Equals(rType, "LEASE", StringComparison.OrdinalIgnoreCase))
                                           ? "RENTAL" : "BUY_SELL",
                        Category = rCategory,
                        Locality = rArea,
                        BudgetMax = rBudget
                    };

                    var propertySide = MapToPropertyMatchSide(dummyProperty);
                    var requirementSide = MapToRequirementMatchSide(dummyRequirement);

                    adminItems.Add(new UserMatchItemDto
                    {
                        Id = reader["matchid"]?.ToString() ?? Guid.NewGuid().ToString(),
                        Title = propertySide.Title,
                        Description = requirementSide.Title,
                        TransactionType = mappedTxType,
                        Category = lCategory,
                        City = "Indore",
                        Locality = lArea,
                        BudgetMin = lPrice,
                        BudgetMax = rBudget,
                        PostedAt = postedAt,
                        PostedTimeAgo = GetTimeAgoText(postedAt),
                        MatchScore = matchScore,
                        IsUnlocked = true, // Admin sees everything unlocked
                        UnlockStatus = "UNLOCKED",
                        OwnerContact = new ContactDetailsDto
                        {
                            OwnerName = lBrokerName,
                            OwnerMobile = lBrokerMobile,
                            OwnerEmail = null
                        },
                        Property = propertySide,
                        Requirement = requirementSide
                    });
                }

                if (!wasOpen) await conn.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching stored procedure matches for admin.");
            }

            return new UserMatchesResponseDto
            {
                Success = true,
                TotalCount = adminItems.Count,
                Data = adminItems
            };
        }

        // 1. Fetch user's own property requests to use as search criteria
        var userQuery = _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId);

        if (!string.IsNullOrEmpty(targetTxType))
        {
            userQuery = userQuery.Where(p => p.TransactionType == targetTxType);
        }

        var userRequests = await userQuery.ToListAsync();

        // 2. Query property requests posted by OTHER users (p.UserId != userId)
        var otherQuery = _dbContext.PropertyRequests
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.UserId != userId);

        if (!string.IsNullOrEmpty(targetTxType))
        {
            otherQuery = otherQuery.Where(p => p.TransactionType == targetTxType);
        }

        var otherPropertyRequests = await otherQuery
            .OrderByDescending(p => p.PostedAt)
            .ToListAsync();

        // 3. Fetch set of property IDs unlocked by this user
        var unlockedPropertyIds = (await _dbContext.UnlockedProperties
            .AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.PropertyRequestId)
            .ToListAsync())
            .ToHashSet();

        // Fetch all pending BROKER_UNLOCK notifications involving User A (userId)
        var pendingUnlockNotifications = await _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.Type == "BROKER_UNLOCK" && !n.IsContactUnlocked &&
                        (n.UserId == userId || (n.MetaJson != null && n.MetaJson.Contains(userId.ToString()))))
            .ToListAsync();

        var sentPendingPropertyIds = new HashSet<Guid>();
        var receivedPendingPropertyIds = new HashSet<Guid>();

        foreach (var n in pendingUnlockNotifications)
        {
            if (string.IsNullOrEmpty(n.MetaJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(n.MetaJson);
                var root = doc.RootElement;
                if ((root.TryGetProperty("initiatorUserId", out var initUserProp) || root.TryGetProperty("InitiatorUserId", out initUserProp)) && 
                    (root.TryGetProperty("initiatorPropertyRequestId", out var initPropProp) || root.TryGetProperty("InitiatorPropertyRequestId", out initPropProp)) && 
                    (root.TryGetProperty("targetPropertyRequestId", out var targetPropProp) || root.TryGetProperty("TargetPropertyRequestId", out targetPropProp)))
                {
                    var initUserId = Guid.Parse(initUserProp.GetString()!);
                    var initPropId = Guid.Parse(initPropProp.GetString()!);
                    var targetPropId = Guid.Parse(targetPropProp.GetString()!);

                    if (initUserId == userId)
                    {
                        sentPendingPropertyIds.Add(targetPropId);
                    }
                    else if (n.UserId == userId)
                    {
                        receivedPendingPropertyIds.Add(initPropId);
                    }
                }
            }
            catch {}
        }

        // 4. Calculate matches and mask sensitive contact data unless unlocked
        var items = new List<UserMatchItemDto>();

        foreach (var propertyRequest in otherPropertyRequests)
        {
            var isUnlocked = unlockedPropertyIds.Contains(propertyRequest.Id);
            var matchScore = CalculateMatchScore(propertyRequest, userRequests);

            // Only include relevant matches (score >= 50)
            if (matchScore >= 50)
            {
                var bestMatchingUserRequest = FindBestMatchingUserRequest(propertyRequest, userRequests);
                if (bestMatchingUserRequest == null) continue; // Require actual matching request to prevent duplication!

                PropertyMatchSideDto propertySide;
                RequirementMatchSideDto requirementSide;

                if (string.Equals(propertyRequest.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
                {
                    propertySide = MapToPropertyMatchSide(propertyRequest);
                    requirementSide = MapToRequirementMatchSide(bestMatchingUserRequest);
                }
                else
                {
                    propertySide = MapToPropertyMatchSide(bestMatchingUserRequest);
                    requirementSide = MapToRequirementMatchSide(propertyRequest);
                }

                string unlockStatus = "NONE";
                if (isUnlocked)
                {
                    unlockStatus = "UNLOCKED";
                }
                else if (sentPendingPropertyIds.Contains(propertyRequest.Id))
                {
                    unlockStatus = "PENDING";
                }
                else if (receivedPendingPropertyIds.Contains(propertyRequest.Id))
                {
                    unlockStatus = "REQUESTED";
                }

                items.Add(new UserMatchItemDto
                {
                    Id = propertyRequest.Id.ToString(),
                    Title = propertySide.Title,
                    Description = requirementSide.Title,
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
                    UnlockStatus = unlockStatus,
                    OwnerContact = isUnlocked ? new ContactDetailsDto
                    {
                        OwnerName = propertyRequest.User?.Name ?? "Property Owner",
                        OwnerMobile = propertyRequest.User?.MobileNumber ?? "N/A",
                        OwnerEmail = propertyRequest.User?.Email
                    } : null,
                    Property = propertySide,
                    Requirement = requirementSide
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

        var oppositeListingType = string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase) ? "DEMAND" : "SUPPLY";
        Guid? userMatchingRequestId = await _dbContext.PropertyRequests
            .Where(p => p.UserId == userId && 
                        p.Status == "ACTIVE" && 
                        p.ListingType == oppositeListingType &&
                        p.Category == targetProperty.Category)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync();

        if (!userMatchingRequestId.HasValue)
        {
            userMatchingRequestId = await _dbContext.PropertyRequests
                .Where(p => p.UserId == userId && p.Status == "ACTIVE")
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();
        }

        if (!userMatchingRequestId.HasValue)
        {
            return new UnlockPropertyResponseDto
            {
                Success = false,
                Message = "You must have an active property listing or requirement to request an unlock.",
                CreditsRemaining = user.Credits
            };
        }

        var userPropId = userMatchingRequestId.Value;
        var targetUserId = targetProperty.UserId;

        // Search for a pending notification sent to User A (userId) from User B (targetUserId)
        var pendingNotificationForUserA = await _dbContext.Notifications
            .Where(n => n.UserId == userId && n.Type == "BROKER_UNLOCK" && n.RequiresTokenUnlock && !n.IsContactUnlocked)
            .ToListAsync();

        Notification? matchedNotification = null;
        foreach (var n in pendingNotificationForUserA)
        {
            if (string.IsNullOrEmpty(n.MetaJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(n.MetaJson);
                var root = doc.RootElement;
                if ((root.TryGetProperty("initiatorUserId", out var initUserProp) || root.TryGetProperty("InitiatorUserId", out initUserProp)) && 
                    (root.TryGetProperty("targetPropertyRequestId", out var targetPropProp) || root.TryGetProperty("TargetPropertyRequestId", out targetPropProp)))
                {
                    var initUserId = Guid.Parse(initUserProp.GetString()!);
                    var targetPropId = Guid.Parse(targetPropProp.GetString()!);

                    if (initUserId == targetUserId && targetPropId == userPropId)
                    {
                        matchedNotification = n;
                        break;
                    }
                }
            }
            catch {}
        }

        if (matchedNotification != null)
        {
            // Proceed to mutual unlock
            var targetUser = await _dbContext.Users.FindAsync(targetUserId);
            if (targetUser == null)
            {
                throw new KeyNotFoundException("Target owner not found.");
            }

            if (user.Credits < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "Insufficient credits. Please purchase a credit package (1 Token = ₹300) to unlock contact details.",
                    CreditsRemaining = user.Credits
                };
            }

            if (targetUser.Credits < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "The matching user has insufficient credits to accept the unlock.",
                    CreditsRemaining = user.Credits
                };
            }

            // Deduct credits
            user.Credits -= 1;
            targetUser.Credits -= 1;
            user.ModifiedDate = DateTime.UtcNow;
            targetUser.ModifiedDate = DateTime.UtcNow;

            // Save unlock records
            var unlockForUserA = new UnlockedProperty
            {
                UserId = userId,
                PropertyRequestId = request.PropertyRequestId,
                UnlockedAt = DateTime.UtcNow
            };

            var unlockForUserB = new UnlockedProperty
            {
                UserId = targetUserId,
                PropertyRequestId = userPropId,
                UnlockedAt = DateTime.UtcNow
            };

            _dbContext.UnlockedProperties.Add(unlockForUserA);
            _dbContext.UnlockedProperties.Add(unlockForUserB);

            // Update notification
            matchedNotification.IsContactUnlocked = true;

            // Create notification for User B
            var successNotificationForUserB = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = targetUserId,
                Type = "MATCH_UNLOCKED",
                Title = "Match Contact Unlocked",
                Body = $"Congratulations! Contact details with {user.Name} are now unlocked.",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.Notifications.Add(successNotificationForUserB);

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Mutual unlock completed for {UserA} and {UserB}.", userId, targetUserId);

            return new UnlockPropertyResponseDto
            {
                Success = true,
                Message = "Contact details unlocked successfully!",
                CreditsRemaining = user.Credits,
                UnlockedContact = new ContactDetailsDto
                {
                    OwnerName = targetProperty.User?.Name ?? "Property Owner",
                    OwnerMobile = targetProperty.User?.MobileNumber ?? "N/A",
                    OwnerEmail = targetProperty.User?.Email
                }
            };
        }
        else
        {
            // Check if User A has already sent an unlock request to User B to avoid duplication
            var existingSentNotification = await _dbContext.Notifications
                .Where(n => n.UserId == targetUserId && n.Type == "BROKER_UNLOCK" && n.RequiresTokenUnlock && !n.IsContactUnlocked)
                .ToListAsync();

            bool alreadyRequested = false;
            foreach (var n in existingSentNotification)
            {
                if (string.IsNullOrEmpty(n.MetaJson)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(n.MetaJson);
                    var root = doc.RootElement;
                    if ((root.TryGetProperty("initiatorUserId", out var initUserProp) || root.TryGetProperty("InitiatorUserId", out initUserProp)) && 
                        (root.TryGetProperty("targetPropertyRequestId", out var targetPropProp) || root.TryGetProperty("TargetPropertyRequestId", out targetPropProp)))
                    {
                        var initUserId = Guid.Parse(initUserProp.GetString()!);
                        var targetPropId = Guid.Parse(targetPropProp.GetString()!);

                        if (initUserId == userId && targetPropId == request.PropertyRequestId)
                        {
                            alreadyRequested = true;
                            break;
                        }
                    }
                }
                catch {}
            }

            if (alreadyRequested)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = true,
                    Message = "Unlock request is already pending with the owner.",
                    CreditsRemaining = user.Credits,
                    UnlockedContact = null
                };
            }

            // Check credits of User A before sending a request to verify eligibility
            if (user.Credits < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "Insufficient credits. You need at least 1 credit to request an unlock.",
                    CreditsRemaining = user.Credits
                };
            }

            // Create pending unlock request
            var initiatorName = user.Name ?? "Another Broker";
            var metaData = new
            {
                initiatorUserId = userId.ToString(),
                initiatorPropertyRequestId = userPropId.ToString(),
                targetPropertyRequestId = request.PropertyRequestId.ToString(),
                brokerId = userId.ToString()
            };

            var pendingNotificationForUserB = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = targetUserId,
                Type = "BROKER_UNLOCK",
                Title = "Unlock Request from Match",
                Body = $"{initiatorName} wants to unlock contact details for your matching property in {targetProperty.Locality}. Click unlock to reveal their details.",
                RequiresTokenUnlock = true,
                IsContactUnlocked = false,
                TokenCost = 1,
                MetaJson = JsonSerializer.Serialize(metaData),
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Notifications.Add(pendingNotificationForUserB);
            await _dbContext.SaveChangesAsync();

            return new UnlockPropertyResponseDto
            {
                Success = true,
                Message = "Unlock request sent to the matching owner. Waiting for their unlock approval.",
                CreditsRemaining = user.Credits,
                UnlockedContact = null
            };
        }
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

        var userRequests = await _dbContext.PropertyRequests
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.Status == "ACTIVE")
            .ToListAsync();

        var items = new List<UserMatchItemDto>();

        foreach (var record in unlockedRecords)
        {
            if (record.PropertyRequest != null)
            {
                PropertyMatchSideDto propertySide;
                RequirementMatchSideDto requirementSide;

                var targetProperty = record.PropertyRequest;
                var oppositeListingType = string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase) ? "DEMAND" : "SUPPLY";
                
                var userMatchingRequest = userRequests
                    .FirstOrDefault(p => string.Equals(p.ListingType, oppositeListingType, StringComparison.OrdinalIgnoreCase) && 
                                         string.Equals(p.Category, targetProperty.Category, StringComparison.OrdinalIgnoreCase));

                if (string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
                {
                    propertySide = MapToPropertyMatchSide(targetProperty);
                    requirementSide = userMatchingRequest != null
                        ? MapToRequirementMatchSide(userMatchingRequest)
                        : MapToRequirementMatchSide(targetProperty);
                }
                else
                {
                    propertySide = userMatchingRequest != null
                        ? MapToPropertyMatchSide(userMatchingRequest)
                        : MapToPropertyMatchSide(targetProperty);
                    requirementSide = MapToRequirementMatchSide(targetProperty);
                }

                 items.Add(new UserMatchItemDto
                {
                    Id = record.PropertyRequest.Id.ToString(),
                    Title = propertySide.Title,
                    Description = requirementSide.Title,
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
                    UnlockStatus = "UNLOCKED",
                    OwnerContact = new ContactDetailsDto
                    {
                        OwnerName = record.PropertyRequest.User?.Name ?? "Property Owner",
                        OwnerMobile = record.PropertyRequest.User?.MobileNumber ?? "N/A",
                        OwnerEmail = record.PropertyRequest.User?.Email
                    },
                    Property = propertySide,
                    Requirement = requirementSide
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

    private PropertyRequest? FindBestMatchingUserRequest(PropertyRequest otherRequest, List<PropertyRequest> userRequests)
    {
        if (userRequests == null || userRequests.Count == 0) return null;

        PropertyRequest? best = null;
        int maxScore = -1;

        foreach (var req in userRequests)
        {
            var score = CalculatePairMatchScore(req, otherRequest);
            if (score > maxScore)
            {
                maxScore = score;
                best = req;
            }
        }

        return best;
    }

    private int CalculatePairMatchScore(PropertyRequest req, PropertyRequest target)
    {
        var score = 50;

        if (string.Equals(req.Category, target.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }

        bool isTransactionMatch = string.Equals(req.TransactionType, target.TransactionType, StringComparison.OrdinalIgnoreCase) ||
                                 (string.Equals(req.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) && string.Equals(target.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase)) ||
                                 (string.Equals(req.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase) && string.Equals(target.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase));

        if (isTransactionMatch)
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

        if (req.BudgetMin.HasValue && target.BudgetMax.HasValue && req.BudgetMin <= target.BudgetMax)
        {
            score += 5;
        }

        return Math.Min(score, 100);
    }

    private PropertyMatchSideDto MapToPropertyMatchSide(PropertyRequest pr)
    {
        var title = pr.Title ?? string.Empty;
        
        // 1. Parse BHK
        var bhk = "2BHK";
        if (title.Contains("1BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("1 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "1BHK";
        else if (title.Contains("3BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("3 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "3BHK";
        else if (title.Contains("4BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("4 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "4BHK";

        // 2. Parse PropertyType
        var propertyType = "Flat";
        if (title.Contains("Penthouse", StringComparison.OrdinalIgnoreCase)) propertyType = "Penthouse";
        else if (title.Contains("Villa", StringComparison.OrdinalIgnoreCase)) propertyType = "Villa";
        else if (title.Contains("Office", StringComparison.OrdinalIgnoreCase)) propertyType = "Office Space";
        else if (title.Contains("Shop", StringComparison.OrdinalIgnoreCase)) propertyType = "Shop";

        // 3. Parse Furnishing
        var furnishing = "Semi-Furnished";
        if (title.Contains("fully furnished", StringComparison.OrdinalIgnoreCase) || title.Contains("fully-furnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Fully Furnished";
        else if (title.Contains("unfurnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Unfurnished";

        // 4. Category Header
        var txText = string.Equals(pr.TransactionType, "RENTAL", StringComparison.OrdinalIgnoreCase) ? "For Rent" : "Sale";
        var categoryHeader = $"{pr.Category} {txText}";

        // 5. Details Line
        string detailsLine;
        if (string.Equals(pr.Category, "Plot/Land", StringComparison.OrdinalIgnoreCase) || 
            title.Contains("Plot", StringComparison.OrdinalIgnoreCase) || 
            title.Contains("Land", StringComparison.OrdinalIgnoreCase))
        {
            detailsLine = "Plot/Land";
        }
        else
        {
            detailsLine = $"{propertyType} - {bhk} {furnishing}";
        }

        // 6. Price Label
        var price = pr.BudgetMin ?? 0;
        string priceLabel;
        if (string.Equals(pr.TransactionType, "RENTAL", StringComparison.OrdinalIgnoreCase))
        {
            priceLabel = price > 0 ? $"{price:N0}/- per month" : "Contact for Price";
        }
        else
        {
            if (price >= 10000000) priceLabel = $"{price/10000000.0:0.##} Cr";
            else if (price >= 100000) priceLabel = $"{price/100000.0:0.##} L";
            else priceLabel = price > 0 ? $"{price:N0}" : "Contact for Price";
        }

        return new PropertyMatchSideDto
        {
            CategoryHeader = categoryHeader,
            DetailsLine = detailsLine,
            Locality = pr.Locality,
            PriceLabel = priceLabel,
            Title = title,
            Description = title
        };
    }

    private RequirementMatchSideDto MapToRequirementMatchSide(PropertyRequest pr)
    {
        var title = pr.Title ?? string.Empty;

        // 1. Parse BHK
        var bhk = "2BHK";
        if (title.Contains("1BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("1 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "1BHK";
        else if (title.Contains("3BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("3 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "3BHK";
        else if (title.Contains("4BHK", StringComparison.OrdinalIgnoreCase) || title.Contains("4 bhk", StringComparison.OrdinalIgnoreCase)) bhk = "4BHK";

        // 2. Parse PropertyType
        var propertyType = "Flat";
        if (title.Contains("Penthouse", StringComparison.OrdinalIgnoreCase)) propertyType = "Penthouse";
        else if (title.Contains("Villa", StringComparison.OrdinalIgnoreCase)) propertyType = "Villa";
        else if (title.Contains("Office", StringComparison.OrdinalIgnoreCase)) propertyType = "Office Space";
        else if (title.Contains("Shop", StringComparison.OrdinalIgnoreCase)) propertyType = "Shop";

        // 3. Parse Furnishing
        var furnishing = "Semi-Furnished";
        if (title.Contains("fully furnished", StringComparison.OrdinalIgnoreCase) || title.Contains("fully-furnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Fully Furnished";
        else if (title.Contains("unfurnished", StringComparison.OrdinalIgnoreCase)) furnishing = "Unfurnished";

        // 4. Category Header
        var txText = string.Equals(pr.TransactionType, "RENTAL", StringComparison.OrdinalIgnoreCase) ? "For Rent" : "Sale";
        var categoryHeader = $"{pr.Category} {propertyType} {txText}";

        // 5. Details Line
        string detailsLine;
        if (string.Equals(pr.Category, "Plot/Land", StringComparison.OrdinalIgnoreCase) || 
            title.Contains("Plot", StringComparison.OrdinalIgnoreCase) || 
            title.Contains("Land", StringComparison.OrdinalIgnoreCase))
        {
            detailsLine = "Plot/Land";
        }
        else
        {
            detailsLine = $"{propertyType} - {bhk} {furnishing}";
        }

        // 6. Price Label
        var price = pr.BudgetMax ?? 0;
        string priceLabel;
        if (string.Equals(pr.TransactionType, "RENTAL", StringComparison.OrdinalIgnoreCase))
        {
            priceLabel = price > 0 ? $"{price:N0} / per month" : "Contact for Budget";
        }
        else
        {
            if (price >= 10000000) priceLabel = $"{price/10000000.0:0.##} Cr";
            else if (price >= 100000) priceLabel = $"{price/100000.0:0.##} L";
            else priceLabel = price > 0 ? $"{price:N0}" : "Contact for Budget";
        }

        return new RequirementMatchSideDto
        {
            CategoryHeader = categoryHeader,
            DetailsLine = detailsLine,
            Locality = pr.Locality,
            PriceLabel = priceLabel,
            Title = title,
            Description = title
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

            bool isTransactionMatch = string.Equals(req.TransactionType, target.TransactionType, StringComparison.OrdinalIgnoreCase) ||
                                     (string.Equals(req.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase) && string.Equals(target.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase)) ||
                                     (string.Equals(req.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase) && string.Equals(target.TransactionType, "BUY", StringComparison.OrdinalIgnoreCase));

            if (isTransactionMatch)
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
