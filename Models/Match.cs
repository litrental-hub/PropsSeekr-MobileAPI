using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

/// <summary>Persistent row from public.matches. matchid is the unlock identity.</summary>
public class Match
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int RequirementId { get; set; }
    public int ListingBrokerId { get; set; }
    public int RequirementBrokerId { get; set; }
    public decimal? MatchScore { get; set; }
    public string? Status { get; set; }
    public string State { get; set; } = "matched";
    public DateTime? CreatedAt { get; set; }
    public DateTime? StatusUpdatedAt { get; set; }
}
