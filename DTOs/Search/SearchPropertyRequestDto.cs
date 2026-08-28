namespace PropSeekr.DTOs.Search;

public class SearchPropertyRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL

    /// <summary>
    /// Listing type to filter by. Allowed values: SUPPLY or DEMAND.
    /// </summary>
    public string? ListingType { get; set; }

    public string Category { get; set; } = string.Empty; // RESIDENTIAL, COMMERCIAL, etc.
    public LocationDto Location { get; set; } = new();
    public string SearchQuery { get; set; } = string.Empty;
    public BudgetFilterDto? Budget { get; set; }
    public FiltersDto Filters { get; set; } = new();
    public PaginationDto Pagination { get; set; } = new();

    public void Validate()
    {
        var listingType = ListingType?.Trim().ToUpperInvariant();
        if (listingType is not ("SUPPLY" or "DEMAND"))
            throw new ArgumentException("listingType must be SUPPLY or DEMAND.");

        var transactionType = TransactionType.Trim().ToUpperInvariant();
        if (transactionType is not ("RENTAL" or "RENT" or "BUY_SELL" or "BUY" or "SELL" or "SALE"))
            throw new ArgumentException("transactionType must be RENTAL or BUY_SELL.");

        if (Location is null || Location.Lat is < -90 or > 90 || Location.Lng is < -180 or > 180)
            throw new ArgumentException("A valid search location is required.");

        if (Location.RadiusKm is <= 0 or > 100)
            throw new ArgumentException("radiusKm must be greater than 0 and no more than 100.");

        if (Pagination.Page < 1)
            Pagination.Page = 1;
        Pagination.Limit = Math.Clamp(Pagination.Limit, 1, 50);
    }
}
