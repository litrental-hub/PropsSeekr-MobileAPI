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
    public string? MatchTier { get; set; }
    public string? ScoreBreakdownJson { get; set; }
    public string? Status { get; set; }
    public string State { get; set; } = "matched";
    public DateTime? CreatedAt { get; set; }
    public DateTime? StatusUpdatedAt { get; set; }

    // AI verification fields
    public string? AiStatus { get; set; }
    public decimal? AiConfidencePct { get; set; }
    public string? AiReasoning { get; set; }
    public string? AiFlagsJson { get; set; }
    public DateTime? AiValidatedAt { get; set; }

    // Navigation properties
    public Listing? Listing { get; set; }
    public Requirement? Requirement { get; set; }
    public Broker? ListingBroker { get; set; }
    public Broker? RequirementBroker { get; set; }
}
