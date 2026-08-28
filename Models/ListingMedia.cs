using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("listing_media")]
public sealed class ListingMedia
{
    [Key]
    [Column("media_id")]
    public long Id { get; set; }

    [Column("listing_id")]
    public int ListingId { get; set; }

    [Column("media_type")]
    [MaxLength(20)]
    public string MediaType { get; set; } = "image";

    [Column("storage_path")]
    [MaxLength(500)]
    public string StoragePath { get; set; } = string.Empty;

    [Column("original_file_name")]
    [MaxLength(255)]
    public string? OriginalFileName { get; set; }

    [Column("mime_type")]
    [MaxLength(100)]
    public string MimeType { get; set; } = "application/octet-stream";

    [Column("file_size_bytes")]
    public long FileSizeBytes { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
