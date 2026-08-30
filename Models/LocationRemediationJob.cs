using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("location_remediation_jobs")]
public sealed class LocationRemediationJob
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("requested_by_user_id")] public Guid RequestedByUserId { get; set; }
    [Column("default_city"), MaxLength(100)] public string DefaultCity { get; set; } = "Indore";
    [Column("status"), MaxLength(20)] public string Status { get; set; } = "queued";
    [Column("stage"), MaxLength(20)] public string Stage { get; set; } = "master";
    [Column("cursor_id")] public int CursorId { get; set; }
    [Column("batch_size")] public int BatchSize { get; set; } = 25;
    [Column("master_resolved")] public int MasterResolved { get; set; }
    [Column("listings_resolved")] public int ListingsResolved { get; set; }
    [Column("requirements_resolved")] public int RequirementsResolved { get; set; }
    [Column("review_required")] public int ReviewRequired { get; set; }
    [Column("lock_token")] public Guid? LockToken { get; set; }
    [Column("locked_at")] public DateTime? LockedAt { get; set; }
    [Column("available_at")] public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    [Column("last_error"), MaxLength(2000)] public string? LastError { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Column("completed_at")] public DateTime? CompletedAt { get; set; }
}
