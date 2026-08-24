namespace PropSeekr.DTOs.Inventory;

public sealed class BrokerListingDto
{
    public string Id { get; set; } = string.Empty;
    public int ListingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string ListingType { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public string? PriceUnit { get; set; }
    public decimal? BuiltUpSize { get; set; }
    public List<BrokerListingSizeDto> Sizes { get; set; } = new();
    public string Status { get; set; } = string.Empty;

    // View tracking is not present in the canonical database yet.
    public int? Views { get; set; }

    public int MatchCount { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class BrokerListingSizeDto
{
    public string Label { get; set; } = string.Empty;
    public decimal SizeSqft { get; set; }
}
