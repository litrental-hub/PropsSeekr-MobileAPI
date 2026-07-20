namespace PropSeekr.DTOs.Search;

public class SearchPropertyRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL

    /// <summary>
    /// Listing type to filter by. Allowed values: SUPPLY or DEMAND.
    /// </summary>
    public string ListingType { get; set; } = string.Empty;

    public LocationDto Location { get; set; } = new();
    public string SearchQuery { get; set; } = string.Empty;
    public FiltersDto Filters { get; set; } = new();
    public PaginationDto Pagination { get; set; } = new();
}
