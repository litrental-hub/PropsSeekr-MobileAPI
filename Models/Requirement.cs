using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("requirements")]
public class Requirement
{
    [Key]
    [Column("requirementid")]
    public int Id { get; set; }

    [Column("broker_id")]
    public int BrokerId { get; set; }
    [ForeignKey("BrokerId")]
    public Broker? Broker { get; set; }

    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    [Column("raw_message_text")]
    public string? RawMessageText { get; set; }

    [Column("requirement_type")]
    [Required]
    [MaxLength(50)]
    public string RequirementType { get; set; } = string.Empty;

    [Column("property_type")]
    [MaxLength(50)]
    public string? PropertyType { get; set; }

    // Arrays map to string[] and int[] in Npgsql
    [Column("configurations")]
    public string[]? Configurations { get; set; }

    [Column("preferred_locality_ids")]
    public int[]? PreferredLocalityIds { get; set; }

    [Column("budget")]
    public decimal? Budget { get; set; }

    [Column("budget_unit")]
    [MaxLength(50)]
    public string? BudgetUnit { get; set; }

    [Column("size")]
    public decimal? Size { get; set; }

    [Column("furnishing_pref")]
    [MaxLength(50)]
    public string? FurnishingPref { get; set; }

    [Column("facing_pref")]
    [MaxLength(50)]
    public string? FacingPref { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string? Status { get; set; }

    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("content_hash")]
    [MaxLength(255)]
    public string? ContentHash { get; set; }

    [Column("group_name")]
    [MaxLength(255)]
    public string? GroupName { get; set; }

    [Column("message_datetime")]
    public DateTime? MessageDatetime { get; set; }

    [Column("budget_type")]
    public string? BudgetType { get; set; }

    [Column("last_confirmed_at")]
    public DateTime? LastConfirmedAt { get; set; }

    [Column("freshness_score")]
    public int? FreshnessScore { get; set; }

    [Column("freshness_category")]
    [MaxLength(50)]
    public string? FreshnessCategory { get; set; }

    [Column("freshness_updated_at")]
    public DateTime? FreshnessUpdatedAt { get; set; }

    [Column("city")]
    [MaxLength(100)]
    public string? City { get; set; }

    [Column("posted_by")]
    [MaxLength(50)]
    public string? PostedBy { get; set; }
}
