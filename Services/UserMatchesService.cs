using Microsoft.EntityFrameworkCore;
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
        int brokerId,
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
            .Include(m => m.RequirementBroker)
            .Where(m => m.ListingBrokerId == brokerId || m.RequirementBrokerId == brokerId);

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

        var counterpartyIds = matches
            .Select(m => m.ListingBrokerId == brokerId ? m.RequirementBrokerId : m.ListingBrokerId)
            .Distinct()
            .ToArray();
        var counterpartyEmails = await _db.Users
            .AsNoTracking()
            .Where(u => u.BrokerId.HasValue && counterpartyIds.Contains(u.BrokerId.Value))
            .GroupBy(u => u.BrokerId!.Value)
            .Select(g => new { BrokerId = g.Key, Email = g.Select(u => u.Email).FirstOrDefault() })
            .ToDictionaryAsync(x => x.BrokerId, x => x.Email);

        var now = DateTime.UtcNow;
        var items = matches.Select(match =>
        {
            var matchConfirmations = confirmations.Where(c => c.MatchId == match.Id).ToList();
            var callerConfirmation = matchConfirmations.FirstOrDefault(c => c.BrokerId == brokerId);
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
            var counterpartyId = match.ListingBrokerId == brokerId
                ? match.RequirementBrokerId
                : match.ListingBrokerId;
            var counterparty = match.ListingBrokerId == brokerId
                ? match.RequirementBroker
                : match.ListingBroker;
            ContactDetailsDto? contact = null;
            if (isRevealed && counterparty is not null)
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
        int brokerId,
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
            CurrentBrokerConfirmed = callerConfirmation?.ConfirmedAt.HasValue == true &&
                                     callerConfirmation.WindowExpiresAt > DateTime.UtcNow,
            WindowExpiresAt = activeExpiry,
            IsRevealed = isRevealed,
            UnlockedContact = contact,
            ConnectionRequestId = connectionRequest?.Id,
            ConnectionRequestStatus = connectionRequest?.Status,
            DeliveryChannel = connectionRequest?.DeliveryChannel,
            IncomingConnectionRequest = connectionRequest is
            {
                Status: ConnectionRequestStatuses.Pending
            } && connectionRequest.ReceivingBrokerId == brokerId,
            CurrentBrokerRole = match.ListingBrokerId == brokerId ? "listing" : "requirement",
            IsUnlocked = isRevealed,
            OwnerContact = contact,
            UnlockStatus = isRevealed ? "UNLOCKED" : state.ToUpperInvariant(),
            Title = title,
            Description = listing?.RawMessageText ?? string.Empty,
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
                Description = listing?.RawMessageText ?? string.Empty
            },
            Requirement = new RequirementMatchSideDto
            {
                CategoryHeader = requirement?.PropertyType ?? string.Empty,
                DetailsLine = requirementTitle,
                Locality = requirement?.City ?? string.Empty,
                PriceLabel = FormatMoney(requirement?.Budget, requirement?.BudgetUnit),
                Title = requirementTitle,
                Description = requirement?.RawMessageText ?? string.Empty
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
}
