using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Matches;

public class UnlockPropertyRequestDto
{
    [Required]
    public int MatchId { get; set; }
}

public class UnlockPropertyResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int CreditsRemaining { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
}

public class MatchConfirmationRequestDto
{
    [Required]
    public int MatchId { get; set; }

    [Range(1, int.MaxValue)]
    public int BrokerId { get; set; }
    
    public bool AvailabilityConfirmed { get; set; }
    public bool PriceValid { get; set; }
    public bool PriceNegotiable { get; set; }
    public bool ReadyToConnect { get; set; }
}

public class MatchConfirmationResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int MatchId { get; set; }
    public string State { get; set; } = string.Empty; // matched, pending_confirmation, confirmed, expired
    public DateTime? WindowExpiresAt { get; set; }
    public int CreditsRequired { get; set; }
}

