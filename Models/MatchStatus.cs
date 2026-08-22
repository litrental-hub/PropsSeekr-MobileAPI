using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("match_statuses")]
public class MatchStatus
{
    [Key]
    [Column("status_id")]
    public int StatusId { get; set; }

    [Column("status_name")]
    [MaxLength(50)]
    public string StatusName { get; set; } = null!;

    [Column("color_code")]
    [MaxLength(20)]
    public string ColorCode { get; set; } = null!;

    [Column("message")]
    public string Message { get; set; } = null!;

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }
}
