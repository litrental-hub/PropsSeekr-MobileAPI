namespace PropSeekr.DTOs.Inventory;

public class GetMyPropertyListingsResponseDto
{
    public bool Success { get; set; } = true;
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int Limit { get; set; } = 20;
    public List<PropertyListingDto> Data { get; set; } = new();
}
