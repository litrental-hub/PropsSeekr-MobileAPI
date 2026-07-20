using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Matches;

public class UnlockPropertyRequestDto
{
    [Required]
    public Guid PropertyRequestId { get; set; }
}

public class UnlockPropertyResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int CreditsRemaining { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
}
