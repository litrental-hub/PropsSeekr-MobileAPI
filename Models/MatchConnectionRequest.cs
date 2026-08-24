using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("match_connection_requests")]
public sealed class MatchConnectionRequest
{
    [Key]
    [Column("request_id")]
    public long Id { get; set; }

    [Column("match_id")]
    public int MatchId { get; set; }

    [Column("requesting_broker_id")]
    public int RequestingBrokerId { get; set; }

    [Column("receiving_broker_id")]
    public int ReceivingBrokerId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = ConnectionRequestStatuses.Pending;

    [Column("delivery_channel")]
    [MaxLength(20)]
    public string DeliveryChannel { get; set; } = "in_app";

    [Column("delivery_status")]
    [MaxLength(30)]
    public string DeliveryStatus { get; set; } = "pending";

    [Column("rejection_reason_code")]
    [MaxLength(50)]
    public string? RejectionReasonCode { get; set; }

    [Column("rejection_reason_text")]
    public string? RejectionReasonText { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("responded_at")]
    public DateTime? RespondedAt { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }
}

public static class ConnectionRequestStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string CreditRequired = "credit_required";
}
