using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("listings")]
public class Listing
{
    [Key]
    [Column("listingid")]
    public int Id { get; set; }

    [Column("broker_id")]
    public int BrokerId { get; set; }
    [ForeignKey("BrokerId")]
    public Broker? Broker { get; set; }

    [Column("master_id")]
    public int? MasterId { get; set; }

    [Column("source")]
    [MaxLength(50)]
    public string? Source { get; set; }

    [Column("raw_message_text")]
    public string? RawMessageText { get; set; }

    [Column("listing_type")]
    [MaxLength(50)]
    public string? ListingType { get; set; }

    [Column("property_type")]
    [MaxLength(50)]
    public string? PropertyType { get; set; }

    [Column("configuration")]
    [MaxLength(50)]
    public string? Configuration { get; set; }

    [Column("price")]
    public decimal? Price { get; set; }

    [Column("price_unit")]
    [MaxLength(50)]
    public string? PriceUnit { get; set; }

    [Column("size")]
    public decimal? Size { get; set; }

    [Column("furnishing")]
    [MaxLength(50)]
    public string? Furnishing { get; set; }

    [Column("facing")]
    [MaxLength(50)]
    public string? Facing { get; set; }

    [Column("floor_number")]
    public int? FloorNumber { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string? Status { get; set; }

    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [Column("last_refreshed_at")]
    public DateTime? LastRefreshedAt { get; set; }

    [Column("last_confirmed_at")]
    public DateTime? LastConfirmedAt { get; set; }

    [Column("freshness_score")]
    public int? FreshnessScore { get; set; }

    [Column("freshness_category")]
    [MaxLength(50)]
    public string? FreshnessCategory { get; set; }

    [Column("freshness_updated_at")]
    public DateTime? FreshnessUpdatedAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("project_name")]
    [MaxLength(255)]
    public string? ProjectName { get; set; }

    [Column("road_info")]
    public string? RoadInfo { get; set; }

    [Column("content_hash")]
    [MaxLength(255)]
    public string? ContentHash { get; set; }

    [Column("group_name")]
    [MaxLength(255)]
    public string? GroupName { get; set; }

    [Column("message_datetime")]
    public DateTime? MessageDatetime { get; set; }

    [Column("price_status")]
    public string? PriceStatus { get; set; }

    [Column("city")]
    [MaxLength(100)]
    public string? City { get; set; }

    [Column("posted_by")]
    [MaxLength(50)]
    public string? PostedBy { get; set; }
}
