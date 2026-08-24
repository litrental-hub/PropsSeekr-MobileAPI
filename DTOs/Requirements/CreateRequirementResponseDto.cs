namespace PropSeekr.DTOs.Requirements;

public class CreateRequirementResponseDto
{
    public bool Success { get; set; } = true;
    public string RequirementId { get; set; } = string.Empty;
    public string Message { get; set; } = "Requirement posted successfully.";
    public int MatchCount { get; set; }
}
