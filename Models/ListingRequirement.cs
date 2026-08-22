using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("listing_requirements")]
public class ListingRequirement
{
    [Key]
    [Column("listing_requirement_id")]
    public int Id { get; set; }

    [Column("listing_id")]
    public int ListingId { get; set; }

    [ForeignKey("ListingId")]
    public Listing? Listing { get; set; }

    [Column("requirement_id")]
    public int RequirementId { get; set; }

    [ForeignKey("RequirementId")]
    public Requirement? Requirement { get; set; }

    [Column("match_status")]
    [MaxLength(50)]
    public string? MatchStatus { get; set; }

    [Column("match_score")]
    public decimal? MatchScore { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
