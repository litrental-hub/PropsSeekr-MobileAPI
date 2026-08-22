using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("deals")]
public class Deal
{
    [Key]
    public int Id { get; set; }

    [Column("match_id")]
    public int MatchId { get; set; }
    [ForeignKey("MatchId")]
    public Match? Match { get; set; }

    [Column("marked_by_broker_id")]
    public int MarkedByBrokerId { get; set; }
    [ForeignKey("MarkedByBrokerId")]
    public Broker? MarkedByBroker { get; set; }

    [Column("deal_value")]
    public decimal? DealValue { get; set; }

    [Column("closed_at")]
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
