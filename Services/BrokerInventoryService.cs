using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>
/// Read model for the Inventory screen. It deliberately reads only canonical
/// broker-owned listings/requirements, then nests their counterpart matches.
/// </summary>
public sealed class BrokerInventoryService : IBrokerInventoryService
{
    private readonly AppDbContext _db;
    private readonly IBrokerIdentityService _brokerIdentityService;

    public BrokerInventoryService(AppDbContext db, IBrokerIdentityService brokerIdentityService)
    {
        _db = db;
        _brokerIdentityService = brokerIdentityService;
    }

    public async Task<GetMyPropertyListingsResponseDto> GetMyListingsWithMatchesAsync(Guid userId, int page, int limit)
    {
        (page, limit) = NormalizePagination(page, limit);
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return new GetMyPropertyListingsResponseDto { Page = page, Limit = limit };
        }

        var query = _db.Listings.AsNoTracking()
            .Where(listing => listing.BrokerId == brokerId.Value);
        var totalCount = await query.CountAsync();
        var listings = await query.OrderByDescending(listing => listing.CreatedAt)
            .Skip((page - 1) * limit).Take(limit).ToListAsync();

        var listingIds = listings.Select(listing => listing.Id).ToArray();
        var matchRows = listingIds.Length == 0
            ? []
            : await _db.Matches.AsNoTracking()
                .Where(match => listingIds.Contains(match.ListingId))
                .Join(_db.Requirements.AsNoTracking(), match => match.RequirementId, requirement => requirement.Id,
                    (match, requirement) => new { match.ListingId, Requirement = requirement, Match = ToRequirementMatch(match, requirement) })
                .Where(row => row.Requirement.BrokerId != brokerId.Value)
                .Select(row => new { row.ListingId, row.Match })
                .ToListAsync();

        var matchesByListing = matchRows.GroupBy(row => row.ListingId)
            .ToDictionary(group => group.Key, group => Sort(group.Select(row => row.Match)));

        var data = listings.Select(listing =>
        {
            var matches = matchesByListing.GetValueOrDefault(listing.Id, []);
            return new PropertyListingDto
            {
                Id = listing.Id.ToString(),
                Title = ListingTitle(listing),
                ListingType = listing.ListingType ?? string.Empty,
                TransactionType = listing.ListingType ?? string.Empty,
                Category = listing.PropertyType ?? string.Empty,
                Price = listing.Price ?? 0,
                BuiltUpSize = listing.Size ?? 0,
                City = listing.City ?? string.Empty,
                Locality = listing.ProjectName ?? string.Empty,
                Status = listing.Status ?? string.Empty,
                CreatedAt = listing.CreatedAt ?? DateTime.MinValue,
                UpdatedAt = listing.UpdatedAt ?? listing.CreatedAt ?? DateTime.MinValue,
                MatchesFound = matches.Count,
                Matches = matches
            };
        }).ToList();

        return new GetMyPropertyListingsResponseDto
        {
            Success = true, TotalCount = totalCount, Page = page, Limit = limit, Data = data
        };
    }

    public async Task<MyRequirementsResponseDto> GetMyRequirementsWithMatchesAsync(Guid userId, int page, int limit)
    {
        (page, limit) = NormalizePagination(page, limit);
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return new MyRequirementsResponseDto { Metadata = new MetadataDto { Page = page, Limit = limit } };
        }

        var query = _db.Requirements.AsNoTracking()
            .Where(requirement => requirement.BrokerId == brokerId.Value);
        var totalCount = await query.CountAsync();
        var requirements = await query.OrderByDescending(requirement => requirement.CreatedAt)
            .Skip((page - 1) * limit).Take(limit).ToListAsync();

        var requirementIds = requirements.Select(requirement => requirement.Id).ToArray();
        var matchRows = requirementIds.Length == 0
            ? []
            : await _db.Matches.AsNoTracking()
                .Where(match => requirementIds.Contains(match.RequirementId))
                .Join(_db.Listings.AsNoTracking(), match => match.ListingId, listing => listing.Id,
                    (match, listing) => new { match.RequirementId, Listing = listing, Match = ToListingMatch(match, listing) })
                .Where(row => row.Listing.BrokerId != brokerId.Value)
                .Select(row => new { row.RequirementId, row.Match })
                .ToListAsync();

        var matchesByRequirement = matchRows.GroupBy(row => row.RequirementId)
            .ToDictionary(group => group.Key, group => Sort(group.Select(row => row.Match)));

        var data = requirements.Select(requirement =>
        {
            var matches = matchesByRequirement.GetValueOrDefault(requirement.Id, []);
            var configuration = requirement.Configurations?.FirstOrDefault() ?? string.Empty;
            var propertyType = requirement.PropertyType ?? "Property";
            var locality = requirement.City ?? "Location not specified";
            return new RequirementListItemDto
            {
                Id = requirement.Id.ToString(),
                RequirementId = requirement.Id.ToString(),
                Description = $"Wants to {(IsRental(requirement.RequirementType) ? "Rent" : "Buy")} {configuration} {propertyType}".Replace("  ", " ").Trim(),
                TransactionType = IsRental(requirement.RequirementType) ? "RENTAL" : "BUY_SELL",
                Category = propertyType,
                PropertyType = propertyType,
                Configuration = configuration,
                Locality = locality,
                Location = locality,
                MatchesFound = matches.Count,
                Matches = matches,
                Budget = new BudgetResponseDto
                {
                    Min = 0, Max = Convert.ToInt64(requirement.Budget ?? 0),
                    DisplayValue = requirement.Budget.HasValue ? $"INR {requirement.Budget.Value:N0}" : "Budget on request",
                    Currency = "INR"
                },
                PreferredLocation = new LocationDto { City = locality, Locality = locality },
                RequiredArea = new RequiredAreaDto
                {
                    Min = Convert.ToInt32(requirement.Size ?? 0), Max = Convert.ToInt32(requirement.Size ?? 0),
                    DisplayValue = requirement.Size.HasValue ? $"{requirement.Size.Value:N0} sq ft" : string.Empty,
                    Unit = "SQFT"
                },
                PostedAt = requirement.CreatedAt ?? DateTime.MinValue,
                Status = requirement.Status ?? "active"
            };
        }).ToList();

        return new MyRequirementsResponseDto
        {
            Success = true,
            Metadata = new MetadataDto { TotalCount = totalCount, Page = page, Limit = limit },
            Data = data
        };
    }

    private static InventoryMatchDto ToRequirementMatch(Match match, Requirement requirement) => new()
    {
        MatchId = match.Id, MatchScore = match.MatchScore, State = match.State, Status = match.Status, MatchedAt = match.CreatedAt,
        CounterpartId = requirement.Id, CounterpartType = "requirement", Title = RequirementTitle(requirement),
        Configuration = requirement.Configurations?.FirstOrDefault(), PropertyType = requirement.PropertyType,
        City = requirement.City, PriceOrBudget = requirement.Budget, PriceOrBudgetUnit = requirement.BudgetUnit,
        Size = requirement.Size, StatusLabel = requirement.Status, LastUpdatedAt = requirement.UpdatedAt
    };

    private static InventoryMatchDto ToListingMatch(Match match, Listing listing) => new()
    {
        MatchId = match.Id, MatchScore = match.MatchScore, State = match.State, Status = match.Status, MatchedAt = match.CreatedAt,
        CounterpartId = listing.Id, CounterpartType = "listing", Title = ListingTitle(listing),
        Configuration = listing.Configuration, PropertyType = listing.PropertyType, City = listing.City, Locality = listing.ProjectName,
        PriceOrBudget = listing.Price, PriceOrBudgetUnit = listing.PriceUnit, Size = listing.Size,
        StatusLabel = listing.Status, LastUpdatedAt = listing.UpdatedAt
    };

    private static List<InventoryMatchDto> Sort(IEnumerable<InventoryMatchDto> matches) => matches
        .OrderByDescending(match => match.MatchScore ?? 0).ThenByDescending(match => match.MatchedAt).ToList();

    private static string ListingTitle(Listing listing) => string.Join(" ", new[] { listing.Configuration, listing.PropertyType }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string RequirementTitle(Requirement requirement) => string.Join(" ", new[] { "Looking for", requirement.Configurations?.FirstOrDefault(), requirement.PropertyType }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsRental(string? value) => value?.Trim().ToUpperInvariant() is "RENT" or "RENTAL" or "LEASE";

    private static (int Page, int Limit) NormalizePagination(int page, int limit) =>
        (page < 1 ? 1 : page, limit is < 1 or > 100 ? 20 : limit);
}
