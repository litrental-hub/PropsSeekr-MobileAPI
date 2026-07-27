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
}
