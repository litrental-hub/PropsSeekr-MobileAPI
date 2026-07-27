namespace PropSeekr.DTOs.Requirements;

public class CreateRequirementRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL
    public string Category { get; set; } = string.Empty; // RESIDENTIAL, COMMERCIAL, etc.
    public string Description { get; set; } = string.Empty;
    public long BudgetMax { get; set; }
    public int MinimumSize { get; set; }
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double RadiusKm { get; set; }
}
