namespace PropSeekr.DTOs.Search;

public class PropertySearchResponseItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string ListingType { get; set; } = string.Empty; // SUPPLY or DEMAND
    public string Category { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public string PostedTimeAgo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double? DistanceKm { get; set; }
    public List<PreferredLocationDto> PreferredLocations { get; set; } = new();
    public BudgetResponseDto? Budget { get; set; }
    public RequiredAreaDto? RequiredArea { get; set; }
    public UrgencyDto? Urgency { get; set; }
    public List<ClientPreferenceDto> ClientPreferences { get; set; } = new();
    public PostedByDto? PostedBy { get; set; }
    public ActionsDto? Actions { get; set; }
}
