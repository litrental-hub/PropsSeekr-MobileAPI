using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

/// <summary>
/// Canonical city/locality catalogue used by nearby search and deterministic matching.
/// </summary>
[Table("master")]
public sealed class MasterLocation
{
    [Key]
    [Column("masterid")]
    public int Id { get; set; }

    [Column("area")]
    public string? Area { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("lat")]
    public double? Latitude { get; set; }

    [Column("lng")]
    public double? Longitude { get; set; }

    [Column("aliases")]
    public string? Aliases { get; set; }

    [Column("geocoding_status")]
    [MaxLength(24)]
    public string GeocodingStatus { get; set; } = "pending";

    [Column("geocoding_provider")]
    [MaxLength(32)]
    public string? GeocodingProvider { get; set; }

    [Column("provider_place_id")]
    [MaxLength(255)]
    public string? ProviderPlaceId { get; set; }

    [Column("formatted_address")]
    [MaxLength(500)]
    public string? FormattedAddress { get; set; }

    [Column("location_precision")]
    [MaxLength(40)]
    public string? LocationPrecision { get; set; }

    [Column("geocoding_confidence")]
    public decimal? GeocodingConfidence { get; set; }

    [Column("geocoded_at")]
    public DateTime? GeocodedAt { get; set; }

    [Column("geocoding_error")]
    [MaxLength(1000)]
    public string? GeocodingError { get; set; }

    [Column("review_required")]
    public bool ReviewRequired { get; set; }
}
