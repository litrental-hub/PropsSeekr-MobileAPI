using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("visits")]
public class Visit
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

    [Column("visit_date")]
    public DateTime? VisitDate { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
