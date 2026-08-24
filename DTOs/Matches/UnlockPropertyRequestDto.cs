using System.ComponentModel.DataAnnotations;
using PropSeekr.Models;

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
    public string? ErrorCode { get; set; }
    public int CreditsRemaining { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
}

public class MatchConfirmationRequestDto
{
    [Required]
    public int MatchId { get; set; }

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
    public string? ErrorCode { get; set; }
    public long? ConnectionRequestId { get; set; }
    public string? ConnectionRequestStatus { get; set; }
    public string? DeliveryChannel { get; set; }
    public string? DeliveryStatus { get; set; }
    public bool? CounterpartyRegistered { get; set; }
    public bool IsRevealed { get; set; }
    public int? CreditsRemaining { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
}

public class MatchRejectionRequestDto
{
    [Required]
    public int MatchId { get; set; }

    public long? ConnectionRequestId { get; set; }

    [Required]
    [MaxLength(50)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? ReasonText { get; set; }
}

public class MatchRejectionResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int MatchId { get; set; }
    public long ConnectionRequestId { get; set; }
    public string ConnectionRequestStatus { get; set; } = ConnectionRequestStatuses.Rejected;
}
