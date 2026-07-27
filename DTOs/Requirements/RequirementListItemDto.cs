using PropSeekr.DTOs.Search;

namespace PropSeekr.DTOs.Requirements;

public class RequirementListItemDto
{
    public string RequirementId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public BudgetResponseDto Budget { get; set; } = new();
    public LocationDto PreferredLocation { get; set; } = new();
    public RequiredAreaDto RequiredArea { get; set; } = new();
    public DateTime PostedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}
