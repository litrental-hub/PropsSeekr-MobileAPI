using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("listing_details")]
public sealed class ListingDetail
{
    [Key]
    [Column("listing_id")]
    public int ListingId { get; set; }

    [Column("details_json", TypeName = "jsonb")]
    public string DetailsJson { get; set; } = "{}";

    [Column("photo_sharing_preference")]
    [MaxLength(30)]
    public string? PhotoSharingPreference { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
