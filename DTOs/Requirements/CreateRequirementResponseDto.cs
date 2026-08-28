namespace PropSeekr.DTOs.Requirements;

public class CreateRequirementResponseDto
{
    public bool Success { get; set; } = true;
    public string RequirementId { get; set; } = string.Empty;
    public string Message { get; set; } = "Requirement posted successfully.";
    public int MatchCount { get; set; }
    public bool EmbeddingCompleted { get; set; }
    public string EmbeddingStatus { get; set; } = "queued";
    public Guid EmbeddingJobId { get; set; }
}
