namespace PropSeekr.DTOs.Search;

public class PostedByDto
{
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Initials { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string Role { get; set; } = "PropSeekr";
    public string? AvatarUrl { get; set; }
}
