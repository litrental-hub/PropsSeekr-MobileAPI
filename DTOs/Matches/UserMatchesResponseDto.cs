namespace PropSeekr.DTOs.Matches;

public class UserMatchesResponseDto
{
    public bool Success { get; set; } = true;
    public int TotalCount { get; set; }
    public List<UserMatchItemDto> Data { get; set; } = new();
}
