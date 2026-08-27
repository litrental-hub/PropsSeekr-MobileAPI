using System;
using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class User
{
    public Guid Id { get; set; }

    /// <summary>Links the authenticated app account to the broker-owned matching and credit data.</summary>
    public int? BrokerId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional for platform administrators. Broker/user registrations are
    /// still required to provide a verified mobile number.
    /// </summary>
    [MaxLength(10)]
    public string? MobileNumber { get; set; }

    /// <summary>
    /// Login name for a platform administrator. Normal users continue to use
    /// their mobile number or email address.
    /// </summary>
    [MaxLength(100)]
    public string? UserName { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(255)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? AddressLine1 { get; set; }

    [MaxLength(255)]
    public string? AddressLine2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(100)]
    public string? State { get; set; }

    [MaxLength(10)]
    public string? Pincode { get; set; }

    [MaxLength(12)]
    public string? AadharNumber { get; set; }

    [MaxLength(10)]
    public string? PanCard { get; set; }

    [MaxLength(20)]
    public string? GSTNumber { get; set; }

    [MaxLength(50)]
    public string? ReraRegistrationNumber { get; set; }

    public string? ProfilePhotoUrl { get; set; }

    public bool IsMobileVerified { get; set; } = false;

    public bool IsEmailVerified { get; set; } = false;

    public int Credits { get; set; } = 0;

    [MaxLength(30)]
    public string Role { get; set; } = "User";

    /// <summary>Inactive identities cannot authenticate, regardless of role.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public Broker? Broker { get; set; }
}
