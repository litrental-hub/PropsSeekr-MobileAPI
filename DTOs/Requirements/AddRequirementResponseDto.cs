using System;

namespace PropSeekr.DTOs.Requirements;

public class AddRequirementResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public AddRequirementDataDto Data { get; set; } = new();
}

public class AddRequirementDataDto
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string LookingFor { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Budget { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int MatchesFound { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
}
