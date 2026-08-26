using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        if (!await _db.Listings.AnyAsync(row => row.Id == listingId, cancellationToken))
            throw new KeyNotFoundException($"Listing ID {listingId} not found.");

        return await RunStoredProcedureAsync(null, listingId, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> RunForRequirementAsync(
        int requirementId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Requirements.AnyAsync(row => row.Id == requirementId, cancellationToken))
            throw new KeyNotFoundException($"Requirement ID {requirementId} not found.");

        return await RunStoredProcedureAsync(requirementId, null, cancellationToken);
    }

    private async Task<IReadOnlyList<int>> RunStoredProcedureAsync(
        int? requirementId,
        int? listingId,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var requirementParameter = new NpgsqlParameter("p_requirement_id", requirementId ?? (object)DBNull.Value);
        var listingParameter = new NpgsqlParameter("p_listing_id", listingId ?? (object)DBNull.Value);

        await _db.Database.ExecuteSqlRawAsync(
            "CALL public.sp_run_matching_engine(@p_requirement_id, @p_listing_id)",
            [requirementParameter, listingParameter],
            cancellationToken);

        var matches = await _db.Matches
            .Where(match =>
                (!requirementId.HasValue || match.RequirementId == requirementId.Value) &&
                (!listingId.HasValue || match.ListingId == listingId.Value) &&
                match.Status == "MATCHED")
            .ToListAsync(cancellationToken);

        var created = matches
            .Where(match => match.CreatedAt.HasValue && match.CreatedAt.Value >= startedAt.AddSeconds(-1))
            .ToList();
        if (created.Count > 0)
        {
            AddMatchNotifications(created);
            await _db.SaveChangesAsync(cancellationToken);
        }

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
