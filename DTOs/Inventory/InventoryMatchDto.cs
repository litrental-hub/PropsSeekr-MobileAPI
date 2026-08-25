namespace PropSeekr.DTOs.Inventory;

/// <summary>
/// Contact-safe counterpart summary nested under an inventory item.
/// Contact details are intentionally available only from the reveal flow.
/// </summary>
public sealed class InventoryMatchDto
{
    public int MatchId { get; set; }
    public decimal? MatchScore { get; set; }
    public string State { get; set; } = string.Empty;
    public string? Status { get; set; }
    public DateTime? MatchedAt { get; set; }

    public int CounterpartId { get; set; }
    public string CounterpartType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Configuration { get; set; }
    public string? PropertyType { get; set; }
    public string? City { get; set; }
    public string? Locality { get; set; }
    public decimal? PriceOrBudget { get; set; }
    public string? PriceOrBudgetUnit { get; set; }
    public decimal? Size { get; set; }
    public string? StatusLabel { get; set; }
    public DateTime? LastUpdatedAt { get; set; }
}
