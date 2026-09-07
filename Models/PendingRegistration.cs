using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

/// <summary>
/// Short-lived registration data. It is never an authenticated account and is
/// promoted to <see cref="User"/> only after both email and mobile OTPs pass.
/// </summary>
[Table("pending_registrations")]
public sealed class PendingRegistration
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100), Column("name")] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(10), Column("mobile_number")] public string MobileNumber { get; set; } = string.Empty;
    [Required, MaxLength(255), Column("email")] public string Email { get; set; } = string.Empty;
    [Required, MaxLength(255), Column("password_hash")] public string PasswordHash { get; set; } = string.Empty;
    [Required, MaxLength(255), Column("address_line1")] public string AddressLine1 { get; set; } = string.Empty;
    [MaxLength(255), Column("address_line2")] public string? AddressLine2 { get; set; }
    [Required, MaxLength(100), Column("city")] public string City { get; set; } = string.Empty;
    [Required, MaxLength(100), Column("state")] public string State { get; set; } = string.Empty;
    [Required, MaxLength(10), Column("pincode")] public string Pincode { get; set; } = string.Empty;
    [Required, MaxLength(12), Column("aadhar_number")] public string AadharNumber { get; set; } = string.Empty;
    [Required, MaxLength(10), Column("pan_card")] public string PanCard { get; set; } = string.Empty;
    [MaxLength(20), Column("gst_number")] public string? GstNumber { get; set; }
    [MaxLength(50), Column("rera_registration_number")] public string? ReraRegistrationNumber { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("expires_at")] public DateTime ExpiresAt { get; set; }
}
