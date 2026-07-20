namespace PropSeekr.DTOs.Search;

public class SearchPropertyRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL
    public string? ListingType { get; set; } // SUPPLY or DEMAND
    public string Category { get; set; } = string.Empty; // RESIDENTIAL, COMMERCIAL, etc.
    public LocationDto Location { get; set; } = new();
    public string SearchQuery { get; set; } = string.Empty;
    public BudgetFilterDto? Budget { get; set; }
    public FiltersDto Filters { get; set; } = new();
    public PaginationDto Pagination { get; set; } = new();
}
