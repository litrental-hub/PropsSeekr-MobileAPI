using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("bulk_import_jobs")]
public sealed class BulkImportJob
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("broker_id")] public int BrokerId { get; set; }
    [Column("storage_key"), MaxLength(500)] public string StorageKey { get; set; } = string.Empty;
    [Column("original_file_name"), MaxLength(255)] public string OriginalFileName { get; set; } = string.Empty;
    [Column("default_city"), MaxLength(100)] public string DefaultCity { get; set; } = "Indore";
    [Column("status"), MaxLength(20)] public string Status { get; set; } = "awaiting_upload";
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("max_attempts")] public int MaxAttempts { get; set; } = 5;
    [Column("available_at")] public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    [Column("locked_at")] public DateTime? LockedAt { get; set; }
    [Column("lock_token")] public Guid? LockToken { get; set; }
    [Column("completed_at")] public DateTime? CompletedAt { get; set; }
    [Column("listings_inserted")] public int ListingsInserted { get; set; }
    [Column("requirements_inserted")] public int RequirementsInserted { get; set; }
    [Column("skipped_records")] public int SkippedRecords { get; set; }
    [Column("failed_records")] public int FailedRecords { get; set; }
    [Column("last_error")] public string? LastError { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Broker? Broker { get; set; }
}
