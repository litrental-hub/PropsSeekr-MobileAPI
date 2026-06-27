namespace PropSeekr.DTOs.Search;

public class SearchPropertyRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL
    public LocationDto Location { get; set; } = new();
    public string SearchQuery { get; set; } = string.Empty;
    public FiltersDto Filters { get; set; } = new();
    public PaginationDto Pagination { get; set; } = new();
}
