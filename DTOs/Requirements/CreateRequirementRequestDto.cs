namespace PropSeekr.DTOs.Requirements;

public class CreateRequirementRequestDto
{
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL
    public string Category { get; set; } = string.Empty; // RESIDENTIAL, COMMERCIAL, etc.
    public string PropertyType { get; set; } = string.Empty;
    public string[] Configurations { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public long BudgetMax { get; set; }
    public long? BudgetMin { get; set; }
    public string BudgetType { get; set; } = "FIXED";
    public int MinimumSize { get; set; }
    public int? MaximumSize { get; set; }
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double RadiusKm { get; set; }
    public List<PreferredLocationDto>? PreferredLocations { get; set; }
    public string[] PreferredProjectNames { get; set; } = [];
    public string? FurnishingPreference { get; set; }
    public string? FacingPreference { get; set; }
    public string? AdditionalNotes { get; set; }
}

public class PreferredLocationDto
{
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
}
