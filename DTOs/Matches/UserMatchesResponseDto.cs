namespace PropSeekr.DTOs.Matches;

public class UserMatchesResponseDto
{
    public bool Success { get; set; } = true;
    public int TotalCount { get; set; }
    public int ExcellentCount { get; set; }
    public int GoodCount { get; set; }
    public int FairCount { get; set; }
    public int UnlockedCount { get; set; }
    public List<UserMatchItemDto> Data { get; set; } = new();
}
