using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class BrokerListingsService : IBrokerListingsService
{
    private static readonly string[] RentalListingTypes = ["RENT", "RENTAL", "LEASE"];
    private static readonly string[] BuySellListingTypes = ["SELL", "SALE", "BUY_SELL", "BUY/SELL", "SUPPLY"];

    private readonly AppDbContext _db;

    public BrokerListingsService(AppDbContext db) => _db = db;

    public async Task<GetBrokerListingsResponseDto> GetMyListingsAsync(
        int brokerId,
        int page,
        int limit,
        string? transactionType = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (brokerId <= 0) throw new ArgumentOutOfRangeException(nameof(brokerId));
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

        var query = _db.Listings
            .AsNoTracking()
            .Where(listing => listing.BrokerId == brokerId);

        var normalizedTransactionType = NormalizeTransactionFilter(transactionType);
        if (normalizedTransactionType == "RENTAL")
        {
            query = query.Where(listing =>
                listing.ListingType != null && RentalListingTypes.Contains(listing.ListingType.ToUpper()));
        }
        else if (normalizedTransactionType == "BUY_SELL")
        {
            query = query.Where(listing =>
                listing.ListingType != null && BuySellListingTypes.Contains(listing.ListingType.ToUpper()));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToUpperInvariant();
            query = query.Where(listing =>
                listing.Status != null && listing.Status.ToUpper() == normalizedStatus);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var listingRows = await query
            .OrderByDescending(listing => listing.CreatedAt)
            .ThenByDescending(listing => listing.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(listing => new ListingRow(
                listing.Id,
                listing.ListingType,
                listing.PropertyType,
                listing.Configuration,
                listing.Price,
                listing.PriceUnit,
                listing.Size,
                listing.Status,
                listing.ProjectName,
                listing.City,
                listing.CreatedAt,
                listing.UpdatedAt))
            .ToListAsync(cancellationToken);

        if (listingRows.Count == 0)
        {
            return new GetBrokerListingsResponseDto
            {
                TotalCount = totalCount,
                Page = page,
                Limit = limit
            };
        }

        var listingIds = listingRows.Select(listing => listing.Id).ToArray();

        var sizeRows = await _db.ListingSizes
            .AsNoTracking()
            .Where(size => listingIds.Contains(size.ListingId))
            .OrderBy(size => size.Id)
            .Select(size => new SizeRow(size.ListingId, size.SizeLabel, size.SizeSqft))
            .ToListAsync(cancellationToken);

        // Count only by listing_id. This intentionally avoids materializing Match rows.
        var matchCounts = await _db.Matches
            .AsNoTracking()
            .Where(match => listingIds.Contains(match.ListingId))
            .GroupBy(match => match.ListingId)
            .Select(group => new { ListingId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ListingId, row => row.Count, cancellationToken);

        var sizesByListing = sizeRows
            .GroupBy(size => size.ListingId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(size => new BrokerListingSizeDto
                {
                    Label = size.Label?.Trim() ?? string.Empty,
                    SizeSqft = size.SizeSqft
                }).ToList());

        var items = listingRows.Select(listing =>
        {
            var sizes = sizesByListing.GetValueOrDefault(listing.Id) ?? [];
            var transaction = NormalizeTransactionType(listing.ListingType);
            var location = FirstNonEmpty(listing.ProjectName, listing.City, "Location not specified");
            var propertyType = CleanDisplayText(listing.PropertyType);
            var configuration = CleanDisplayText(listing.Configuration);

            return new BrokerListingDto
            {
                Id = listing.Id.ToString(),
                ListingId = listing.Id,
                Title = BuildTitle(configuration, propertyType, sizes),
                Type = transaction == "RENTAL" ? "RENTAL" : "BUY/SELL",
                TransactionType = transaction,
                ListingType = listing.ListingType?.Trim().ToUpperInvariant() ?? string.Empty,
                PropertyType = propertyType,
                Configuration = configuration,
                Location = location,
                Locality = listing.ProjectName?.Trim() ?? string.Empty,
                City = listing.City?.Trim() ?? string.Empty,
                Price = listing.Price,
                PriceUnit = listing.PriceUnit,
                BuiltUpSize = listing.Size ?? sizes.FirstOrDefault()?.SizeSqft,
                Sizes = sizes,
                Status = NormalizeStatus(listing.Status),
                Views = null,
                MatchCount = matchCounts.GetValueOrDefault(listing.Id),
                CreatedAt = listing.CreatedAt,
                UpdatedAt = listing.UpdatedAt
            };
        }).ToList();

        return new GetBrokerListingsResponseDto
        {
            TotalCount = totalCount,
            Page = page,
            Limit = limit,
            Data = items
        };
    }

    internal static string NormalizeTransactionType(string? listingType)
    {
        var normalized = listingType?.Trim().ToUpperInvariant();
        return normalized != null && RentalListingTypes.Contains(normalized) ? "RENTAL" : "BUY_SELL";
    }

    internal static string BuildTitle(
        string configuration,
        string propertyType,
        IReadOnlyList<BrokerListingSizeDto> sizes)
    {
        var effectiveConfiguration = configuration;
        if (string.IsNullOrWhiteSpace(effectiveConfiguration))
        {
            effectiveConfiguration = sizes
                .Select(size => size.Label)
                .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? string.Empty;
        }

        var parts = new[] { effectiveConfiguration, propertyType }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var title = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(title) ? "Property Listing" : title;
    }

    private static string? NormalizeTransactionFilter(string? transactionType)
    {
        if (string.IsNullOrWhiteSpace(transactionType)) return null;
        var normalized = transactionType.Trim().ToUpperInvariant().Replace('-', '_').Replace('/', '_');
        if (normalized is "RENT" or "RENTAL" or "LEASE") return "RENTAL";
        if (normalized is "BUY_SELL" or "SELL" or "SALE" or "SUPPLY") return "BUY_SELL";
        throw new ArgumentException("transactionType must be RENTAL or BUY_SELL.", nameof(transactionType));
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "Under Review";
        return string.Join(' ', status
            .Trim()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }

    private static string CleanDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Join(' ', value
            .Trim()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.All(char.IsUpper)
                ? word
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.First(candidate => !string.IsNullOrWhiteSpace(candidate))!.Trim();

    private sealed record ListingRow(
        int Id,
        string? ListingType,
        string? PropertyType,
        string? Configuration,
        decimal? Price,
        string? PriceUnit,
        decimal? Size,
        string? Status,
        string? ProjectName,
        string? City,
        DateTime? CreatedAt,
        DateTime? UpdatedAt);

    private sealed record SizeRow(int ListingId, string? Label, decimal SizeSqft);
}
