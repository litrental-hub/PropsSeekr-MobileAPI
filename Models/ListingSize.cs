using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("listing_sizes")]
public class ListingSize
{
    [Key]
    [Column("listingsizeid")]
    public int Id { get; set; }

    [Column("listing_id")]
    public int ListingId { get; set; }

    [ForeignKey("ListingId")]
    public Listing? Listing { get; set; }

    [Column("size_sqft")]
    public decimal SizeSqft { get; set; }

    [Column("size_label")]
    [MaxLength(255)]
    public string? SizeLabel { get; set; }
}
