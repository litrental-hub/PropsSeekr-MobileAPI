using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("embedding_jobs")]
public class EmbeddingJob
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("entity_type")]
    [MaxLength(20)]
    public string EntityType { get; set; } = string.Empty;

    [Column("entity_id")]
    public int EntityId { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "queued";

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("max_attempts")]
    public int MaxAttempts { get; set; } = 5;

    [Column("available_at")]
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;

    [Column("locked_at")]
    public DateTime? LockedAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
