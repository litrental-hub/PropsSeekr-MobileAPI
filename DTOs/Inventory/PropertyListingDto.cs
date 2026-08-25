namespace PropSeekr.DTOs.Inventory;

public class PropertyListingDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ListingType { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal BuiltUpSize { get; set; }
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MatchesFound { get; set; }
    public List<InventoryMatchDto> Matches { get; set; } = new();
}
