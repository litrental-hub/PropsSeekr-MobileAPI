using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using NetTopologySuite.Geometries;
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

    public async Task<UserMatchesResponseDto> GetUserMatchesAsync(Guid userId, string? transactionType = null, int page = 1, int limit = 20, double? lat = null, double? lng = null)
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
                    sqlFilter = "AND (l.listing_type IN ('RENT', 'LEASE')) AND (r.requirement_type IN ('RENT', 'LEASE'))";
                }
                else if (targetTxType == "BUY_SELL")
                {
                    sqlFilter = "AND l.listing_type = 'SELL' AND r.requirement_type = 'BUY'";
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
                      AND l.broker_id <> r.broker_id
                      AND m.match_score >= 69
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
                        IsUnlocked = false,
                        UnlockStatus = "locked",
                        OwnerContact = null,
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

            var pagedAdminItems = adminItems.Skip((page - 1) * limit).Take(limit).ToList();
            return new UserMatchesResponseDto
            {
                Success = true,
                TotalCount = adminItems.Count,
                Data = pagedAdminItems
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

        // Fetch all handshakes involving this user's property requests in legacy tables
        var userRequestIdStrings = userRequests.Select(r => r.Id.ToString()).ToList();
        var brokerPhone = user.MobileNumber;
        var broker = await _dbContext.Brokers.AsNoTracking().FirstOrDefaultAsync(b => b.PhoneNumber == brokerPhone);

        var unlockedPairs = new HashSet<(Guid, Guid)>();
        var sentPendingPairs = new HashSet<(Guid, Guid)>();
        var receivedPendingPairs = new HashSet<(Guid, Guid)>();

        if (broker != null)
        {
            var confirmations = await _dbContext.MatchConfirmations
                .AsNoTracking()
                .Include(c => c.Match)
                .ThenInclude(m => m!.Listing)
                .Include(c => c.Match)
                .ThenInclude(m => m!.Requirement)
                .Where(c => (c.Match!.Listing != null && userRequestIdStrings.Contains(c.Match.Listing.ContentHash!)) || 
                            (c.Match!.Requirement != null && userRequestIdStrings.Contains(c.Match.Requirement.ContentHash!)))
                .ToListAsync();

            // Group confirmations by MatchId to inspect state
            var confirmationsByMatch = confirmations.GroupBy(c => c.MatchId);
            foreach (var group in confirmationsByMatch)
            {
                var match = group.First().Match;
                if (match == null || match.Listing == null || match.Requirement == null) continue;

                if (!Guid.TryParse(match.Listing.ContentHash, out var listingGuid) || 
                    !Guid.TryParse(match.Requirement.ContentHash, out var requirementGuid))
                {
                    continue;
                }

                var userIsListingOwner = userRequestIdStrings.Contains(match.Listing.ContentHash);
                var localGuid = userIsListingOwner ? listingGuid : requirementGuid;
                var otherGuid = userIsListingOwner ? requirementGuid : listingGuid;

                if (string.Equals(match.State, "confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    unlockedPairs.Add((localGuid, otherGuid));
                    unlockedPairs.Add((otherGuid, localGuid));
                }
                else if (string.Equals(match.State, "pending_confirmation", StringComparison.OrdinalIgnoreCase))
                {
                    var localUserConfirmed = group.Any(c => c.BrokerId == broker.Id && c.ConfirmedAt != null);
                    if (localUserConfirmed)
                    {
                        sentPendingPairs.Add((localGuid, otherGuid));
                    }
                    else
                    {
                        receivedPendingPairs.Add((localGuid, otherGuid));
                    }
                }
            }
        }

        // 4. Calculate matches and mask sensitive contact data unless unlocked
        var items = new List<UserMatchItemDto>();

        foreach (var propertyRequest in otherPropertyRequests)
        {
            var matchScore = CalculateMatchScore(propertyRequest, userRequests);

            // Only include relevant matches (score >= 50)
            if (matchScore >= 50)
            {
                var bestMatchingUserRequest = FindBestMatchingUserRequest(propertyRequest, userRequests);
                if (bestMatchingUserRequest == null) continue; // Require actual matching request to prevent duplication!

                // Find if any of the user's requests has been unlocked or has a pending confirmation
                var unlockedRequest = userRequests.FirstOrDefault(r => unlockedPairs.Contains((r.Id, propertyRequest.Id)));
                var isUnlocked = unlockedRequest != null || unlockedPropertyIds.Contains(propertyRequest.Id);

                if (unlockedRequest != null)
                {
                    bestMatchingUserRequest = unlockedRequest;
                }

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

                string unlockStatus = "locked";
                if (isUnlocked)
                {
                    unlockStatus = "matched and confirmed";
                }
                else
                {
                    var sentPendingRequest = userRequests.FirstOrDefault(r => sentPendingPairs.Contains((r.Id, propertyRequest.Id)));
                    if (sentPendingRequest != null)
                    {
                        unlockStatus = "pending";
                        bestMatchingUserRequest = sentPendingRequest;
                        
                        // Re-map property sides with the aligned initiator request
                        if (string.Equals(propertyRequest.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
                        {
                            requirementSide = MapToRequirementMatchSide(bestMatchingUserRequest);
                        }
                        else
                        {
                            propertySide = MapToPropertyMatchSide(bestMatchingUserRequest);
                        }
                    }
                    else
                    {
                        var receivedPendingRequest = userRequests.FirstOrDefault(r => receivedPendingPairs.Contains((r.Id, propertyRequest.Id)));
                        if (receivedPendingRequest != null)
                        {
                            unlockStatus = "matched";
                            bestMatchingUserRequest = receivedPendingRequest;

                            // Re-map property sides with the aligned initiator request
                            if (string.Equals(propertyRequest.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
                            {
                                requirementSide = MapToRequirementMatchSide(bestMatchingUserRequest);
                            }
                            else
                            {
                                propertySide = MapToPropertyMatchSide(bestMatchingUserRequest);
                            }
                        }
                    }
                }

                double? distanceKm = null;
                string distanceLabel = string.Empty;
                if (lat.HasValue && lng.HasValue && propertyRequest.Location != null)
                {
                    var dist = GetDistanceKm(propertyRequest.Location, new Point(lng.Value, lat.Value) { SRID = 4326 });
                    distanceKm = dist;
                    distanceLabel = $"{dist:0.0} km";
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
                    PropertyRequestId = propertyRequest.Id,
                    InitiatorPropertyRequestId = bestMatchingUserRequest.Id,
                    OwnerContact = isUnlocked ? new ContactDetailsDto
                    {
                        OwnerName = propertyRequest.User?.Name ?? "Property Owner",
                        OwnerMobile = propertyRequest.User?.MobileNumber ?? "N/A",
                        OwnerEmail = propertyRequest.User?.Email
                    } : null,
                    Property = propertySide,
                    Requirement = requirementSide,
                    DistanceKm = distanceKm,
                    DistanceLabel = distanceLabel
                });
            }
        }

        // Sort and apply location fallback logic
        if (lat.HasValue && lng.HasValue)
        {
            var nearbyItems = items.Where(i => i.DistanceKm.HasValue && i.DistanceKm.Value <= 15.0).OrderBy(i => i.DistanceKm).ToList();
            if (nearbyItems.Any())
            {
                items = nearbyItems;
            }
            else
            {
                var userCity = userRequests.FirstOrDefault()?.City ?? "Indore";
                items = items
                    .Where(i => i.City != null && i.City.Equals(userCity, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.MatchScore)
                    .ThenByDescending(i => i.PostedAt)
                    .Take(5)
                    .ToList();
            }
        }
        else
        {
            items = items.OrderByDescending(i => i.MatchScore).ThenByDescending(i => i.PostedAt).ToList();
        }

        var pagedItems = items.Skip((page - 1) * limit).Take(limit).ToList();
        return new UserMatchesResponseDto
        {
            Success = true,
            TotalCount = items.Count,
            Data = pagedItems
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

        Guid? userMatchingRequestId = null;
        if (request.InitiatorPropertyRequestId.HasValue)
        {
            var initPropExists = await _dbContext.PropertyRequests
                .AnyAsync(p => p.Id == request.InitiatorPropertyRequestId.Value && p.UserId == userId && (p.Status == "ACTIVE" || p.Status == "LOOKING"));
            if (initPropExists)
            {
                userMatchingRequestId = request.InitiatorPropertyRequestId.Value;
            }
        }

        if (!userMatchingRequestId.HasValue)
        {
            var oppositeListingType = string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase) ? "DEMAND" : "SUPPLY";
            userMatchingRequestId = await _dbContext.PropertyRequests
                .Where(p => p.UserId == userId && 
                            (p.Status == "ACTIVE" || p.Status == "LOOKING") && 
                            p.ListingType == oppositeListingType &&
                            p.Category == targetProperty.Category)
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
        var userProperty = await _dbContext.PropertyRequests.FindAsync(userPropId);
        if (userProperty == null)
        {
            throw new KeyNotFoundException("Matching user property request not found.");
        }

        // Get legacy match and brokers
        var match = await GetOrCreateLegacyMatchAsync(targetProperty, userProperty);
        var (brokerA, walletA) = await GetOrCreateLegacyBrokerAsync(user);
        
        var targetUser = await _dbContext.Users.FindAsync(targetProperty.UserId);
        if (targetUser == null)
        {
            throw new KeyNotFoundException("Target user not found.");
        }
        var (brokerB, walletB) = await GetOrCreateLegacyBrokerAsync(targetUser);

        // Check if match is already confirmed
        if (string.Equals(match.State, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            // Already unlocked, ensure UnlockedProperty entry exists
            var existingUserUnlock = await _dbContext.UnlockedProperties
                .FirstOrDefaultAsync(u => u.UserId == userId && u.PropertyRequestId == request.PropertyRequestId);
            if (existingUserUnlock == null)
            {
                var unlock = new UnlockedProperty
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PropertyRequestId = request.PropertyRequestId,
                    UnlockedAt = DateTime.UtcNow
                };
                _dbContext.UnlockedProperties.Add(unlock);
                await _dbContext.SaveChangesAsync();
            }

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

        // Load existing confirmations for this match
        var confirmations = await _dbContext.MatchConfirmations
            .Where(c => c.MatchId == match.Id)
            .ToListAsync();

        var localConfirmation = confirmations.FirstOrDefault(c => c.BrokerId == brokerA.Id);
        var otherConfirmation = confirmations.FirstOrDefault(c => c.BrokerId == brokerB.Id);

        if (localConfirmation != null)
        {
            // Already pending counterparty confirmation
            return new UnlockPropertyResponseDto
            {
                Success = true,
                Message = "Unlock request is already pending with the owner.",
                CreditsRemaining = user.Credits,
                UnlockedContact = null
            };
        }

        if (otherConfirmation != null)
        {
            // Check if the 4-hour window has expired
            if (otherConfirmation.WindowExpiresAt.HasValue && otherConfirmation.WindowExpiresAt.Value < DateTime.UtcNow)
            {
                using var dbTxEx = await _dbContext.Database.BeginTransactionAsync();
                try
                {
                    match.State = "matched";
                    match.Status = "matched";
                    match.StatusUpdatedAt = DateTime.UtcNow;

                    _dbContext.MatchConfirmations.RemoveRange(confirmations);
                    await _dbContext.SaveChangesAsync();
                    await dbTxEx.CommitAsync();
                }
                catch (Exception ex)
                {
                    await dbTxEx.RollbackAsync();
                    _logger.LogError(ex, "Error resetting expired match confirmations.");
                }

                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "The 4-hour confirmation window has expired. Please try connecting again.",
                    CreditsRemaining = user.Credits
                };
            }

            // Phase 2 (Accept/Confirm)
            var totalA = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance;
            var totalB = walletB.FreeCreditsBalance + walletB.PaidCreditsBalance;

            if (totalA < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "Insufficient credits. Please purchase a credit package (1 Token = ₹300) to unlock contact details.",
                    CreditsRemaining = user.Credits
                };
            }

            if (totalB < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "The requesting user has insufficient credits to accept the unlock.",
                    CreditsRemaining = user.Credits
                };
            }

            using var dbTx = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // Create confirmation for local user
                localConfirmation = new MatchConfirmation
                {
                    MatchId = match.Id,
                    BrokerId = brokerA.Id,
                    AvailabilityConfirmed = request.IsAvailable,
                    PriceValid = request.IsPriceValid,
                    PriceNegotiable = request.IsPriceNegotiable,
                    ReadyToConnect = request.ReadyToConnect,
                    ConfirmedAt = DateTime.UtcNow,
                    WindowExpiresAt = null,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.MatchConfirmations.Add(localConfirmation);

                // Update match status to confirmed
                match.State = "confirmed";
                match.Status = "confirmed";
                match.StatusUpdatedAt = DateTime.UtcNow;

                // Deduct credits from initiator (wallet B)
                if (walletB.FreeCreditsBalance >= 1) walletB.FreeCreditsBalance -= 1;
                else walletB.PaidCreditsBalance -= 1;
                walletB.UpdatedAt = DateTime.UtcNow;
                targetUser.Credits = walletB.FreeCreditsBalance + walletB.PaidCreditsBalance;
                targetUser.ModifiedDate = DateTime.UtcNow;

                // Deduct credits from acceptor (wallet A)
                if (walletA.FreeCreditsBalance >= 1) walletA.FreeCreditsBalance -= 1;
                else walletA.PaidCreditsBalance -= 1;
                walletA.UpdatedAt = DateTime.UtcNow;
                user.Credits = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance;
                user.ModifiedDate = DateTime.UtcNow;

                // Insert into reveals table
                var reveal = new Reveal
                {
                    MatchId = match.Id,
                    RevealedAt = DateTime.UtcNow
                };
                _dbContext.Reveals.Add(reveal);
                await _dbContext.SaveChangesAsync(); // save to get reveal ID if needed

                // Log ledger transactions
                var txA = new CreditTransaction
                {
                    BrokerId = brokerA.Id,
                    Type = "deduct",
                    Amount = 1,
                    BalanceAfter = user.Credits,
                    ReferenceType = "reveal",
                    ReferenceId = reveal.Id,
                    Notes = $"Unlocked contact details via handshake match {match.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                var txB = new CreditTransaction
                {
                    BrokerId = brokerB.Id,
                    Type = "deduct",
                    Amount = 1,
                    BalanceAfter = targetUser.Credits,
                    ReferenceType = "reveal",
                    ReferenceId = reveal.Id,
                    Notes = $"Unlocked contact details via handshake match {match.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.CreditTransactions.AddRange(txA, txB);

                // Update property freshness
                userProperty.LastConfirmedAt = DateTime.UtcNow;
                userProperty.FreshnessScore = 100;
                userProperty.FreshnessCategory = "Recently Confirmed";
                userProperty.AvailabilityStatus = request.IsAvailable ? "Available" : "RentedOrSold";

                targetProperty.LastConfirmedAt = DateTime.UtcNow;
                targetProperty.FreshnessScore = 100;
                targetProperty.FreshnessCategory = "Recently Confirmed";
                targetProperty.AvailabilityStatus = "Available";

                // Save unlock records
                var unlockA = new UnlockedProperty
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUser.Id,
                    PropertyRequestId = request.PropertyRequestId,
                    UnlockedAt = DateTime.UtcNow
                };
                var unlockB = new UnlockedProperty
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PropertyRequestId = userPropId,
                    UnlockedAt = DateTime.UtcNow
                };
                _dbContext.UnlockedProperties.AddRange(unlockA, unlockB);

                // Create success notifications for the mobile app
                var successNotificationA = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUser.Id,
                    Type = "MATCH_UNLOCKED",
                    Title = "Match Contact Unlocked",
                    Body = $"Congratulations! Contact details with {user.Name} are now unlocked via handshake match {match.Id}.",
                    CreatedAt = DateTime.UtcNow
                };
                var successNotificationB = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Type = "MATCH_UNLOCKED",
                    Title = "Match Contact Unlocked",
                    Body = $"Congratulations! Contact details with {targetUser.Name} are now unlocked via handshake match {match.Id}.",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Notifications.AddRange(successNotificationA, successNotificationB);

                // Create success notifications for the legacy notifications table
                var legacySuccessNotificationA = new BrokerNotification
                {
                    BrokerId = brokerA.Id,
                    Type = "match_found",
                    Channel = "in_app",
                    PayloadJson = JsonSerializer.Serialize(new { matchId = match.Id, revealed = true }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                };
                var legacySuccessNotificationB = new BrokerNotification
                {
                    BrokerId = brokerB.Id,
                    Type = "match_found",
                    Channel = "in_app",
                    PayloadJson = JsonSerializer.Serialize(new { matchId = match.Id, revealed = true }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.BrokerNotifications.AddRange(legacySuccessNotificationA, legacySuccessNotificationB);

                await _dbContext.SaveChangesAsync();
                await dbTx.CommitAsync();

                _logger.LogInformation("Mutual handshake unlock completed successfully for legacy match {MatchId}.", match.Id);

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
            catch (Exception ex)
            {
                await dbTx.RollbackAsync();
                _logger.LogError(ex, "Error completing phase 2 handshake unlock.");
                throw;
            }
        }
        else
        {
            // Phase 1 (Initiate Handshake)
            var totalCredits = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance;
            if (totalCredits < 1)
            {
                return new UnlockPropertyResponseDto
                {
                    Success = false,
                    Message = "Insufficient credits. You need at least 1 credit to request an unlock.",
                    CreditsRemaining = user.Credits
                };
            }

            using var dbTx = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // Create confirmation for local user
                localConfirmation = new MatchConfirmation
                {
                    MatchId = match.Id,
                    BrokerId = brokerA.Id,
                    AvailabilityConfirmed = request.IsAvailable,
                    PriceValid = request.IsPriceValid,
                    PriceNegotiable = request.IsPriceNegotiable,
                    ReadyToConnect = request.ReadyToConnect,
                    ConfirmedAt = DateTime.UtcNow,
                    WindowExpiresAt = DateTime.UtcNow.AddHours(4),
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.MatchConfirmations.Add(localConfirmation);

                // Update match status to pending_confirmation
                match.State = "pending_confirmation";
                match.Status = "pending_confirmation";
                match.StatusUpdatedAt = DateTime.UtcNow;

                // Update freshness score on user's listing
                userProperty.LastConfirmedAt = DateTime.UtcNow;
                userProperty.FreshnessScore = 100;
                userProperty.FreshnessCategory = "Recently Confirmed";
                userProperty.AvailabilityStatus = request.IsAvailable ? "Available" : "RentedOrSold";

                // Create pending in-app notification for User B (matching owner)
                var initiatorName = user.Name ?? "Another Broker";
                var metaData = new
                {
                    initiatorUserId = userId.ToString(),
                    initiatorPropertyRequestId = userPropId.ToString(),
                    targetPropertyRequestId = request.PropertyRequestId.ToString(),
                    brokerId = userId.ToString(),
                    matchId = match.Id
                };

                var pendingNotificationForUserB = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = targetProperty.UserId,
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

                // Create pending notification in legacy notifications table
                var legacyPendingNotification = new BrokerNotification
                {
                    BrokerId = brokerB.Id,
                    Type = "confirm_pending",
                    Channel = "in_app",
                    PayloadJson = JsonSerializer.Serialize(new { matchId = match.Id, initiatorBrokerId = brokerA.Id }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.BrokerNotifications.Add(legacyPendingNotification);

                await _dbContext.SaveChangesAsync();
                await dbTx.CommitAsync();

                return new UnlockPropertyResponseDto
                {
                    Success = true,
                    Message = "Unlock request sent to the matching owner. Waiting for their unlock approval.",
                    CreditsRemaining = user.Credits,
                    UnlockedContact = null
                };
            }
            catch (Exception ex)
            {
                await dbTx.RollbackAsync();
                _logger.LogError(ex, "Error initiating phase 1 handshake unlock.");
                throw;
            }
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
            .Where(p => p.UserId == userId && (p.Status == "ACTIVE" || p.Status == "LOOKING"))
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
                    UnlockStatus = "matched and confirmed",
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

    private async Task<(Broker Broker, CreditWallet Wallet)> GetOrCreateLegacyBrokerAsync(User user)
    {
        var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.PhoneNumber == user.MobileNumber);
        if (broker == null)
        {
            broker = new Broker
            {
                PhoneNumber = user.MobileNumber,
                Name = user.Name,
                CreditBalance = user.Credits,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
                ConfirmationComplianceRate = 100.00m,
                VisibilityPenaltyFlag = false
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
                FreeCreditsBalance = user.Credits,
                PaidCreditsBalance = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.CreditWallets.Add(wallet);
            await _dbContext.SaveChangesAsync();
        }

        return (broker, wallet);
    }

    private async Task<Match> GetOrCreateLegacyMatchAsync(PropertyRequest targetProperty, PropertyRequest userProperty)
    {
        var userA = await _dbContext.Users.FindAsync(targetProperty.UserId);
        var userB = await _dbContext.Users.FindAsync(userProperty.UserId);
        if (userA == null || userB == null)
        {
            throw new KeyNotFoundException("Users not found.");
        }

        var (brokerA, _) = await GetOrCreateLegacyBrokerAsync(userA);
        var (brokerB, _) = await GetOrCreateLegacyBrokerAsync(userB);

        Listing legacyListing;
        Requirement legacyRequirement;

        if (string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
        {
            legacyListing = await GetOrCreateLegacyListingAsync(targetProperty, brokerA.Id);
            legacyRequirement = await GetOrCreateLegacyRequirementAsync(userProperty, brokerB.Id);
        }
        else
        {
            legacyListing = await GetOrCreateLegacyListingAsync(userProperty, brokerB.Id);
            legacyRequirement = await GetOrCreateLegacyRequirementAsync(targetProperty, brokerA.Id);
        }

        var match = await _dbContext.Matches.FirstOrDefaultAsync(m => m.ListingId == legacyListing.Id && m.RequirementId == legacyRequirement.Id);
        if (match == null)
        {
            match = new Match
            {
                ListingId = legacyListing.Id,
                RequirementId = legacyRequirement.Id,
                ListingBrokerId = legacyListing.BrokerId,
                RequirementBrokerId = legacyRequirement.BrokerId,
                MatchScore = 100,
                Status = "matched",
                State = "matched",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.Matches.Add(match);
            await _dbContext.SaveChangesAsync();
        }

        return match;
    }

    private async Task<Listing> GetOrCreateLegacyListingAsync(PropertyRequest prop, int brokerId)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ContentHash == prop.Id.ToString());
        if (listing == null)
        {
            listing = new Listing
            {
                BrokerId = brokerId,
                RawMessageText = prop.Title,
                ListingType = "SUPPLY",
                PropertyType = prop.Category,
                Configuration = prop.PropertyTypesJson.Contains("2BHK") ? "2BHK" : "3BHK",
                Price = prop.BudgetMin,
                Status = "active",
                ContentHash = prop.Id.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Listings.Add(listing);
            await _dbContext.SaveChangesAsync();
        }
        return listing;
    }

    private async Task<Requirement> GetOrCreateLegacyRequirementAsync(PropertyRequest prop, int brokerId)
    {
        var requirement = await _dbContext.Requirements.FirstOrDefaultAsync(r => r.ContentHash == prop.Id.ToString());
        if (requirement == null)
        {
            requirement = new Requirement
            {
                BrokerId = brokerId,
                RawMessageText = prop.Title,
                RequirementType = "DEMAND",
                PropertyType = prop.Category,
                Budget = prop.BudgetMax,
                Status = "active",
                ContentHash = prop.Id.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Requirements.Add(requirement);
            await _dbContext.SaveChangesAsync();
        }
        return requirement;
    }

    private static double GetDistanceKm(Point location, Point centre)
    {
        var lat1 = ToRadians(location.Y);
        var lat2 = ToRadians(centre.Y);
        var dLat = ToRadians(centre.Y - location.Y);
        var dLng = ToRadians(centre.X - location.X);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return 6371.0 * c;
    }

    private static double ToRadians(double value) => value * Math.PI / 180.0;
}
