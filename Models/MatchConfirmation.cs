using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class MatchConfirmation
{
    public int Id { get; set; }

    public int MatchId { get; set; }
    public Match? Match { get; set; }

    public int BrokerId { get; set; }
    public Broker? Broker { get; set; }

    // Pre-reveal checklist fields
    public bool? AvailabilityConfirmed { get; set; }
    public bool? PriceValid { get; set; }
    public bool? PriceNegotiable { get; set; }
    public bool? ReadyToConnect { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    public DateTime? WindowExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    // Unique constraint: one confirmation per (match, broker) pair
}
