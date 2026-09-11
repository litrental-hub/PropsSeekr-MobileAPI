using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class MatchConfirmation
{
    public int Id { get; set; }

    public int MatchId { get; set; }
    public Match? Match { get; set; }

    public int BrokerId { get; set; }
    public Broker? Broker { get; set; }

    public long ConnectionRequestId { get; set; }
    public MatchConnectionRequest? ConnectionRequest { get; set; }

    // Pre-reveal checklist fields
    public bool? AvailabilityConfirmed { get; set; }
    public bool? PriceValid { get; set; }
    public bool? PriceNegotiable { get; set; }
    public bool? ReadyToConnect { get; set; }
    public DateTime? AvailabilityDate { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    public DateTime? WindowExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    // One immutable/current proof per broker and connection attempt.
}
