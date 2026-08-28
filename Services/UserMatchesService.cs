using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>
/// Returns matches for the authenticated broker. Contact data is projected only
/// when a row exists in reveals for the match.
/// </summary>
public sealed class UserMatchesService : IUserMatchesService
{
    private readonly AppDbContext _db;
    private readonly IUnlockService _unlockService;
    private readonly IBrokerIdentityService _brokerIdentityService;

    public UserMatchesService(
        AppDbContext db,
        IUnlockService unlockService,
        IBrokerIdentityService brokerIdentityService)
    {
        _db = db;
        _unlockService = unlockService;
        _brokerIdentityService = brokerIdentityService;
    }

    public async Task<UserMatchesResponseDto> GetUserMatchesAsync(
        Guid userId,
        string? transactionType = null,
        int? listingId = null,
        int? requirementId = null,
        int? matchId = null,
        int page = 1,
        int limit = 20)
    {
        var brokerId = await RequireBrokerIdAsync(userId);
        return await QueryMatchesAsync(brokerId, transactionType, listingId, requirementId, matchId, page, limit, onlyRevealed: false);
    }

    public async Task<MatchDetailResponseDto> GetMatchDetailsAsync(Guid userId, int matchId, bool allowAdminAccess = false)
    {
        if (matchId <= 0) throw new ArgumentException("matchId must be greater than zero.");

        int? brokerId = allowAdminAccess ? null : await RequireBrokerIdAsync(userId);
        var match = await _db.Matches
            .AsNoTracking()
            .Include(item => item.Listing)
            .Include(item => item.Requirement)
            .Include(item => item.ListingBroker)
            .Include(item => item.RequirementBroker)
            .SingleOrDefaultAsync(item => item.Id == matchId)
            ?? throw new KeyNotFoundException("Match not found.");

        if (brokerId.HasValue && match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId)
            throw new UnauthorizedAccessException("You are not a party to this match.");

        var isRevealed = await _db.Reveals.AsNoTracking().AnyAsync(item => item.MatchId == match.Id);
        var contactVisible = brokerId.HasValue && isRevealed;
        var listingDetails = await _db.ListingDetails.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ListingId == match.ListingId);
        var media = await _db.ListingMedia.AsNoTracking()
            .Where(item => item.ListingId == match.ListingId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync();
        var sizes = await _db.ListingSizes.AsNoTracking()
            .Where(item => item.ListingId == match.ListingId)
            .OrderBy(item => item.Id)
            .ToListAsync();
        var connectionRequest = await _db.MatchConnectionRequests.AsNoTracking()
            .Where(item => item.MatchId == match.Id)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();

        var currentBrokerConfirmation = brokerId.HasValue
            ? await _db.MatchConfirmations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.MatchId == match.Id && item.BrokerId == brokerId.Value)
            : null;

        ContactDetailsDto? contact = null;
        if (brokerId is int currentBrokerId && isRevealed)
        {
            var counterpartyId = match.ListingBrokerId == currentBrokerId
                ? match.RequirementBrokerId
                : match.ListingBrokerId;
            var counterparty = match.ListingBrokerId == currentBrokerId
                ? match.RequirementBroker
                : match.ListingBroker;
            var email = await _db.Users.AsNoTracking()
                .Where(item => item.BrokerId == counterpartyId)
                .Select(item => item.Email)
                .FirstOrDefaultAsync();
            if (counterparty is not null)
            {
                contact = new ContactDetailsDto
                {
                    OwnerName = counterparty.Name ?? "Counterparty Broker",
                    OwnerMobile = counterparty.PhoneNumber,
                    OwnerEmail = email
                };
            }
        }

        var state = match.State?.ToLowerInvariant() ?? "matched";
        if (state == "pending_confirmation" && currentBrokerConfirmation?.WindowExpiresAt <= DateTime.UtcNow)
            state = "expired";

        var listing = match.Listing;
        var requirement = match.Requirement;
        var detailsJson = ParseDetails(listingDetails?.DetailsJson, contactVisible);

        return new MatchDetailResponseDto
        {
            Data = new MatchDetailDto
            {
                MatchId = match.Id,
                ListingId = match.ListingId,
                RequirementId = match.RequirementId,
                MatchScore = match.MatchScore,
                State = state,
                CurrentBrokerRole = allowAdminAccess ? "admin" : match.ListingBrokerId == brokerId ? "listing" : "requirement",
                CurrentBrokerConfirmed = currentBrokerConfirmation?.ConfirmedAt.HasValue == true &&
                                         currentBrokerConfirmation.WindowExpiresAt > DateTime.UtcNow,
                IsRevealed = isRevealed,
                ConnectionRequestStatus = connectionRequest?.Status,
                UnlockedContact = contact,
                Property = new ListingMatchDetailDto
                {
                    ListingId = match.ListingId,
                    TransactionType = listing?.ListingType,
                    PropertyType = listing?.PropertyType,
                    Configuration = listing?.Configuration,
                    Price = listing?.Price,
                    PriceUnit = listing?.PriceUnit,
                    Size = listing?.Size,
                    Sizes = sizes.Select(item => new ListingSizeDetailDto
                    {
                        SizeSqft = item.SizeSqft,
                        Label = item.SizeLabel
                    }).ToList(),
                    Furnishing = listing?.Furnishing,
                    Facing = listing?.Facing,
                    FloorNumber = listing?.FloorNumber,
                    Status = listing?.Status,
                    ProjectName = listing?.ProjectName,
                    Locality = listing?.ProjectName,
                    RoadInfo = listing?.RoadInfo,
                    City = listing?.City,
                    Description = ContactRedaction.Redact(listing?.RawMessageText, contactVisible),
                    PhotoSharingPreference = listingDetails?.PhotoSharingPreference,
                    Details = detailsJson,
                    Media = media.Select(item => new ListingMediaDto
                    {
                        MediaId = item.Id,
                        MediaType = item.MediaType,
                        Url = $"/user-matches/matches/{match.Id}/media/{item.Id}",
                        MimeType = item.MimeType,
                        FileSizeBytes = item.FileSizeBytes,
                        SortOrder = item.SortOrder
                    }).ToList(),
                    CreatedAt = listing?.CreatedAt,
                    UpdatedAt = listing?.UpdatedAt
                },
                Requirement = new RequirementMatchDetailDto
                {
                    RequirementId = match.RequirementId,
                    TransactionType = requirement?.RequirementType,
                    PropertyType = requirement?.PropertyType,
                    Configurations = requirement?.Configurations ?? [],
                    Budget = requirement?.Budget,
                    BudgetMin = requirement?.BudgetMin,
                    BudgetType = requirement?.BudgetType,
                    BudgetUnit = requirement?.BudgetUnit,
                    Size = requirement?.Size,
                    SizeMax = requirement?.SizeMax,
                    RadiusKm = requirement?.RadiusKm,
                    PreferredProjectNames = requirement?.PreferredProjectNames ?? [],
                    FurnishingPreference = requirement?.FurnishingPref,
                    FacingPreference = requirement?.FacingPref,
                    Status = requirement?.Status,
                    City = requirement?.City,
                    Description = ContactRedaction.Redact(requirement?.RawMessageText, contactVisible),
                    CreatedAt = requirement?.CreatedAt,
                    UpdatedAt = requirement?.UpdatedAt
                }
            }
        };
    }

    public Task<UserMatchesResponseDto> GetAllMatchesAsync(
        string? transactionType = null,
        int? listingId = null,
        int? requirementId = null,
        int? matchId = null,
        int page = 1,
        int limit = 20) =>
        QueryMatchesAsync(null, transactionType, listingId, requirementId, matchId, page, limit, onlyRevealed: false);

    public async Task<UnlockPropertyResponseDto> UnlockPropertyAsync(Guid userId, UnlockPropertyRequestDto request)
    {
        var brokerId = await RequireBrokerIdAsync(userId);
        return await _unlockService.UnlockMatchAsync(brokerId, request);
    }

    public async Task<UserMatchesResponseDto> GetUnlockedPropertiesAsync(Guid userId, int page = 1, int limit = 20)
    {
        var brokerId = await RequireBrokerIdAsync(userId);
        return await QueryMatchesAsync(brokerId, transactionType: null, listingId: null, requirementId: null, matchId: null, page, limit, onlyRevealed: true);
    }

    private async Task<UserMatchesResponseDto> QueryMatchesAsync(
        int? brokerId,
        string? transactionType,
        int? listingId,
        int? requirementId,
        int? matchId,
        int page,
        int limit,
        bool onlyRevealed)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);

        IQueryable<Match> query = _db.Matches
            .AsNoTracking()
            .Include(m => m.Listing)
            .Include(m => m.Requirement)
            .Include(m => m.ListingBroker)
            .Include(m => m.RequirementBroker);

        if (brokerId.HasValue)
            query = query.Where(m => m.ListingBrokerId == brokerId.Value || m.RequirementBrokerId == brokerId.Value);

        if (onlyRevealed)
        {
            query = query.Where(m => _db.Reveals.Any(r => r.MatchId == m.Id));
        }

        if (listingId.HasValue)
        {
            query = query.Where(m => m.ListingId == listingId.Value);
        }

        if (requirementId.HasValue)
        {
            query = query.Where(m => m.RequirementId == requirementId.Value);
        }

        if (matchId.HasValue)
        {
            query = query.Where(m => m.Id == matchId.Value);
        }

        var normalizedType = transactionType?.Trim().ToUpperInvariant();
        if (normalizedType is "RENT" or "RENTAL" or "RENTALS")
        {
            query = query.Where(m =>
                (m.Listing != null && m.Listing.ListingType != null &&
                 (m.Listing.ListingType.ToUpper() == "RENT" || m.Listing.ListingType.ToUpper() == "RENTAL" || m.Listing.ListingType.ToUpper() == "LEASE")) ||
                (m.Requirement != null &&
                 (m.Requirement.RequirementType.ToUpper() == "RENT" || m.Requirement.RequirementType.ToUpper() == "RENTAL" || m.Requirement.RequirementType.ToUpper() == "LEASE")));
        }
        else if (normalizedType is "BUY_SELL" or "BUY" or "SALE" or "SELL")
        {
            query = query.Where(m =>
                !((m.Listing != null && m.Listing.ListingType != null &&
                   (m.Listing.ListingType.ToUpper() == "RENT" || m.Listing.ListingType.ToUpper() == "RENTAL" || m.Listing.ListingType.ToUpper() == "LEASE")) ||
                  (m.Requirement != null &&
                   (m.Requirement.RequirementType.ToUpper() == "RENT" || m.Requirement.RequirementType.ToUpper() == "RENTAL" || m.Requirement.RequirementType.ToUpper() == "LEASE"))));
        }

        var counts = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Excellent = group.Count(match => match.MatchScore >= 90),
                Good = group.Count(match => match.MatchScore >= 75 && match.MatchScore < 90),
                Fair = group.Count(match => !match.MatchScore.HasValue || match.MatchScore < 75)
            })
            .SingleOrDefaultAsync();
        var unlockedCount = await query.CountAsync(match => _db.Reveals.Any(reveal => reveal.MatchId == match.Id));
        var totalCount = counts?.Total ?? 0;
        var matches = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var matchIds = matches.Select(m => m.Id).ToArray();
        var confirmations = await _db.MatchConfirmations
            .AsNoTracking()
            .Where(c => matchIds.Contains(c.MatchId))
            .ToListAsync();
        var revealedSet = (await _db.Reveals
                .AsNoTracking()
                .Where(r => matchIds.Contains(r.MatchId))
                .Select(r => r.MatchId)
                .ToListAsync())
            .ToHashSet();
        var connectionRequests = await _db.MatchConnectionRequests
            .AsNoTracking()
            .Where(request => matchIds.Contains(request.MatchId))
            .OrderByDescending(request => request.Id)
            .ToListAsync();

        var brokerIds = brokerId.HasValue
            ? matches.Select(m => m.ListingBrokerId == brokerId.Value ? m.RequirementBrokerId : m.ListingBrokerId)
            : matches.SelectMany(m => new[] { m.ListingBrokerId, m.RequirementBrokerId })
            .Distinct()
            .ToArray();
        var counterpartyEmails = await _db.Users
            .AsNoTracking()
            .Where(u => u.BrokerId.HasValue && brokerIds.Contains(u.BrokerId.Value))
            .GroupBy(u => u.BrokerId!.Value)
            .Select(g => new { BrokerId = g.Key, Email = g.Select(u => u.Email).FirstOrDefault() })
            .ToDictionaryAsync(x => x.BrokerId, x => x.Email);

        var now = DateTime.UtcNow;
        var items = matches.Select(match =>
        {
            var matchConfirmations = confirmations.Where(c => c.MatchId == match.Id).ToList();
            var callerConfirmation = brokerId.HasValue
                ? matchConfirmations.FirstOrDefault(c => c.BrokerId == brokerId.Value)
                : null;
            var activeExpiry = matchConfirmations
                .Where(c => c.ConfirmedAt.HasValue && c.WindowExpiresAt.HasValue)
                .Select(c => c.WindowExpiresAt)
                .OrderBy(x => x)
                .FirstOrDefault();
            var state = match.State?.ToLowerInvariant() ?? "matched";
            if (state == "pending_confirmation" && activeExpiry.HasValue && activeExpiry.Value <= now)
            {
                state = "expired";
            }

            var isRevealed = revealedSet.Contains(match.Id);
            var counterpartyId = brokerId.HasValue && match.ListingBrokerId == brokerId.Value
                ? match.RequirementBrokerId
                : match.ListingBrokerId;
            var counterparty = brokerId.HasValue
                ? (match.ListingBrokerId == brokerId.Value ? match.RequirementBroker : match.ListingBroker)
                : null;
            ContactDetailsDto? contact = null;
            if (brokerId.HasValue && isRevealed && counterparty is not null)
            {
                counterpartyEmails.TryGetValue(counterpartyId, out var email);
                contact = new ContactDetailsDto
                {
                    OwnerName = counterparty.Name ?? "Counterparty Broker",
                    OwnerMobile = counterparty.PhoneNumber,
                    OwnerEmail = email
                };
            }

            var connectionRequest = connectionRequests.FirstOrDefault(request => request.MatchId == match.Id);
            return MapMatch(match, brokerId, state, callerConfirmation, activeExpiry, isRevealed, contact, connectionRequest);
        }).ToList();

        return new UserMatchesResponseDto
        {
            Success = true,
            TotalCount = totalCount,
            ExcellentCount = counts?.Excellent ?? 0,
            GoodCount = counts?.Good ?? 0,
            FairCount = counts?.Fair ?? 0,
            UnlockedCount = unlockedCount,
            Data = items
        };
    }

    private static UserMatchItemDto MapMatch(
        Match match,
        int? brokerId,
        string state,
        MatchConfirmation? callerConfirmation,
        DateTime? activeExpiry,
        bool isRevealed,
        ContactDetailsDto? contact,
        MatchConnectionRequest? connectionRequest)
    {
        var listing = match.Listing;
        var requirement = match.Requirement;
        var transactionType = listing?.ListingType ?? requirement?.RequirementType ?? string.Empty;
        var contactVisible = brokerId.HasValue && isRevealed;
        var title = string.Join(" ", new[] { listing?.Configuration, listing?.PropertyType }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(title)) title = listing?.ProjectName ?? "Available Property";
        var requirementTitle = string.Join(" / ", requirement?.Configurations ?? Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(requirementTitle)) requirementTitle = requirement?.PropertyType ?? "Client Requirement";

        return new UserMatchItemDto
        {
            MatchId = match.Id,
            ListingId = match.ListingId,
            RequirementId = match.RequirementId,
            Id = match.Id.ToString(),
            State = state,
            CurrentBrokerConfirmed = brokerId.HasValue && callerConfirmation?.ConfirmedAt.HasValue == true &&
                                     callerConfirmation.WindowExpiresAt > DateTime.UtcNow,
            WindowExpiresAt = activeExpiry,
            IsRevealed = isRevealed,
            UnlockedContact = contact,
            ConnectionRequestId = connectionRequest?.Id,
            ConnectionRequestStatus = connectionRequest?.Status,
            DeliveryChannel = connectionRequest?.DeliveryChannel,
            IncomingConnectionRequest = brokerId.HasValue && connectionRequest is
            {
                Status: ConnectionRequestStatuses.Pending
            } && connectionRequest.ReceivingBrokerId == brokerId,
            CurrentBrokerRole = !brokerId.HasValue ? "admin" : match.ListingBrokerId == brokerId.Value ? "listing" : "requirement",
            IsUnlocked = isRevealed,
            OwnerContact = contact,
            UnlockStatus = isRevealed ? "UNLOCKED" : state.ToUpperInvariant(),
            Title = title,
            Description = ContactRedaction.Redact(listing?.RawMessageText, contactVisible),
            TransactionType = transactionType,
            Category = listing?.PropertyType ?? requirement?.PropertyType ?? string.Empty,
            City = listing?.City ?? requirement?.City ?? string.Empty,
            Locality = listing?.ProjectName ?? requirement?.City ?? string.Empty,
            BudgetMin = listing?.Price is decimal price ? DecimalToLong(price) : null,
            BudgetMax = requirement?.Budget is decimal budget ? DecimalToLong(budget) : null,
            PostedAt = match.CreatedAt ?? DateTime.UtcNow,
            PostedTimeAgo = FormatTimeAgo(match.CreatedAt),
            MatchScore = match.MatchScore.HasValue ? (int)Math.Round(match.MatchScore.Value) : 0,
            Property = new PropertyMatchSideDto
            {
                CategoryHeader = listing?.Configuration ?? string.Empty,
                DetailsLine = title,
                Locality = listing?.ProjectName ?? listing?.City ?? string.Empty,
                PriceLabel = FormatMoney(listing?.Price, listing?.PriceUnit),
                Title = title,
                Description = ContactRedaction.Redact(listing?.RawMessageText, contactVisible)
            },
            Requirement = new RequirementMatchSideDto
            {
                CategoryHeader = requirement?.PropertyType ?? string.Empty,
                DetailsLine = requirementTitle,
                Locality = requirement?.City ?? string.Empty,
                PriceLabel = FormatMoney(requirement?.Budget, requirement?.BudgetUnit),
                Title = requirementTitle,
                Description = ContactRedaction.Redact(requirement?.RawMessageText, contactVisible)
            }
        };
    }

    private async Task<int> RequireBrokerIdAsync(Guid userId) =>
        await _brokerIdentityService.GetBrokerIdAsync(userId)
        ?? throw new KeyNotFoundException("No broker profile is linked to this account.");

    private static long DecimalToLong(decimal value) =>
        value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : decimal.ToInt64(value);

    private static string FormatMoney(decimal? value, string? unit) =>
        value.HasValue ? $"{value.Value:0.##} {unit ?? "INR"}" : "Price on request";

    private static string FormatTimeAgo(DateTime? createdAt)
    {
        if (!createdAt.HasValue) return string.Empty;
        var age = DateTime.UtcNow - createdAt.Value;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static JsonElement ParseDetails(string? value, bool contactRevealed)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value) ?? new JsonObject();
            RedactNode(node, contactRevealed);
            return JsonSerializer.SerializeToElement(node);
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private static void RedactNode(JsonNode node, bool contactRevealed)
    {
        if (contactRevealed) return;
        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(item => item.Key).ToList())
            {
                var child = jsonObject[key];
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    jsonObject[key] = ContactRedaction.Redact(text, contactRevealed: false);
                else if (child is not null)
                    RedactNode(child, contactRevealed: false);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                var child = jsonArray[index];
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    jsonArray[index] = ContactRedaction.Redact(text, contactRevealed: false);
                else if (child is not null)
                    RedactNode(child, contactRevealed: false);
            }
        }
    }
}
