using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class AutomatedMatchingService : IAutomatedMatchingService
{
    private static readonly string[] RentalListingTypes = ["RENT", "RENTAL", "LEASE"];
    private static readonly string[] RentalRequirementTypes = ["RENT", "RENTAL", "LEASE"];
    private static readonly string[] BuySellListingTypes = ["SELL", "SALE", "BUY_SELL", "BUY/SELL", "SUPPLY"];
    private static readonly string[] BuySellRequirementTypes = ["BUY", "SELL", "SALE", "BUY_SELL", "PURCHASE"];

    private readonly AppDbContext _db;

    public AutomatedMatchingService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<int>> RunForListingAsync(
        int listingId,
        CancellationToken cancellationToken = default)
    {
        var listing = await _db.Listings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == listingId, cancellationToken)
            ?? throw new KeyNotFoundException($"Listing ID {listingId} not found.");

        var requirementTypes = IsRentalListing(listing.ListingType)
            ? RentalRequirementTypes
            : BuySellRequirementTypes;
        var propertyTypes = EquivalentPropertyTypes(listing.PropertyType);

        var candidates = await _db.Requirements.AsNoTracking()
            .Where(requirement =>
                requirement.BrokerId != listing.BrokerId &&
                requirement.Status != null && requirement.Status.ToLower() == "active" &&
                requirement.RequirementType != null && requirementTypes.Contains(requirement.RequirementType.ToUpper()) &&
                requirement.PropertyType != null && propertyTypes.Contains(requirement.PropertyType.ToUpper()))
            .Where(requirement =>
                !requirement.Budget.HasValue || !listing.Price.HasValue || requirement.Budget.Value >= listing.Price.Value)
            .ToListAsync(cancellationToken);

        candidates = candidates
            .Where(requirement => ConfigurationMatches(listing.Configuration, requirement.Configurations))
            .ToList();

        var existingRequirementIds = (await _db.Matches.AsNoTracking()
                .Where(match => match.ListingId == listing.Id)
                .Select(match => match.RequirementId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var created = candidates
            .Where(requirement => !existingRequirementIds.Contains(requirement.Id))
            .Select(requirement => NewMatch(listing, requirement))
            .ToList();

        _db.Matches.AddRange(created);
        await _db.SaveChangesAsync(cancellationToken);
        AddMatchNotifications(created);
        await _db.SaveChangesAsync(cancellationToken);
        return created.Select(match => match.Id).ToList();
    }

    public async Task<IReadOnlyList<int>> RunForRequirementAsync(
        int requirementId,
        CancellationToken cancellationToken = default)
    {
        var requirement = await _db.Requirements.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == requirementId, cancellationToken)
            ?? throw new KeyNotFoundException($"Requirement ID {requirementId} not found.");

        var listingTypes = IsRentalRequirement(requirement.RequirementType)
            ? RentalListingTypes
            : BuySellListingTypes;
        var propertyTypes = EquivalentPropertyTypes(requirement.PropertyType);

        var candidates = await _db.Listings.AsNoTracking()
            .Where(listing =>
                listing.BrokerId != requirement.BrokerId &&
                listing.Status != null && listing.Status.ToLower() == "active" &&
                listing.ListingType != null && listingTypes.Contains(listing.ListingType.ToUpper()) &&
                listing.PropertyType != null && propertyTypes.Contains(listing.PropertyType.ToUpper()))
            .Where(listing =>
                !requirement.Budget.HasValue || !listing.Price.HasValue || requirement.Budget.Value >= listing.Price.Value)
            .ToListAsync(cancellationToken);

        candidates = candidates
            .Where(listing => ConfigurationMatches(listing.Configuration, requirement.Configurations))
            .ToList();

        var existingListingIds = (await _db.Matches.AsNoTracking()
                .Where(match => match.RequirementId == requirement.Id)
                .Select(match => match.ListingId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var created = candidates
            .Where(listing => !existingListingIds.Contains(listing.Id))
            .Select(listing => NewMatch(listing, requirement))
            .ToList();

        _db.Matches.AddRange(created);
        await _db.SaveChangesAsync(cancellationToken);
        AddMatchNotifications(created);
        await _db.SaveChangesAsync(cancellationToken);
        return created.Select(match => match.Id).ToList();
    }

    private static Match NewMatch(Listing listing, Requirement requirement) => new()
    {
        ListingId = listing.Id,
        RequirementId = requirement.Id,
        ListingBrokerId = listing.BrokerId,
        RequirementBrokerId = requirement.BrokerId,
        MatchScore = 95m,
        State = "matched",
        Status = "matched",
        CreatedAt = DateTime.UtcNow,
        StatusUpdatedAt = DateTime.UtcNow
    };

    private void AddMatchNotifications(IReadOnlyCollection<Match> createdMatches)
    {
        foreach (var match in createdMatches)
        {
            _db.BrokerNotifications.Add(Notification(match.ListingBrokerId, match, "listing"));
            _db.BrokerNotifications.Add(Notification(match.RequirementBrokerId, match, "requirement"));
        }
    }

    private static BrokerNotification Notification(int brokerId, Match match, string role) => new()
    {
        BrokerId = brokerId,
        Type = "match_found",
        Channel = "in_app",
        PayloadJson = JsonSerializer.Serialize(new
        {
            match_id = match.Id,
            listing_id = match.ListingId,
            requirement_id = match.RequirementId,
            role
        }),
        ChannelStatus = "pending",
        CreatedAt = DateTime.UtcNow
    };

    private static bool IsRentalListing(string? value) =>
        value != null && RentalListingTypes.Contains(value.Trim().ToUpperInvariant());

    private static bool IsRentalRequirement(string? value) =>
        value != null && RentalRequirementTypes.Contains(value.Trim().ToUpperInvariant());

    internal static string[] EquivalentPropertyTypes(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized switch
        {
            "APARTMENT" or "FLAT" or "FLATAPARTMENT" =>
                ["APARTMENT", "FLAT", "FLAT/APARTMENT", "FLAT_APARTMENT", "FLAT APARTMENT"],
            "INDEPENDENTHOUSE" or "HOUSE" =>
                ["INDEPENDENT_HOUSE", "INDEPENDENTHOUSE", "INDEPENDENT HOUSE", "HOUSE"],
            "BUNGALOW" or "VILLA" or "BUNGALOWVILLA" =>
                ["BUNGALOW", "VILLA", "BUNGALOW/VILLA", "BUNGALOW_VILLA", "BUNGALOW VILLA"],
            "PLOT" or "LAND" or "PLOTLAND" =>
                ["PLOT", "LAND", "PLOT/LAND", "PLOT_LAND", "PLOT LAND"],
            "OFFICE" or "OFFICESPACE" =>
                ["OFFICE", "OFFICESPACE", "OFFICE_SPACE", "OFFICE SPACE"],
            "SHOP" or "RETAIL" or "SHOPRETAIL" =>
                ["SHOP", "RETAIL", "SHOP/RETAIL", "SHOP_RETAIL", "SHOP RETAIL"],
            "WAREHOUSE" or "GODOWN" =>
                ["WAREHOUSE", "GODOWN"],
            "PG" or "HOSTEL" or "PGHOSTEL" =>
                ["PG", "HOSTEL", "PG/HOSTEL", "PG_HOSTEL", "PG HOSTEL"],
            _ when !string.IsNullOrWhiteSpace(value) => [value.Trim().ToUpperInvariant()],
            _ => []
        };
    }

    internal static bool ConfigurationMatches(string? listingConfiguration, string[]? requirementConfigurations)
    {
        if (requirementConfigurations is null || requirementConfigurations.Length == 0 ||
            string.IsNullOrWhiteSpace(listingConfiguration))
        {
            return true;
        }

        var listingValue = NormalizeToken(listingConfiguration);
        return requirementConfigurations.Any(value => NormalizeToken(value) == listingValue);
    }

    private static string NormalizeToken(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
