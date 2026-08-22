using System;
using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class User
{
    public Guid Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(10)]
    public string MobileNumber { get; set; } = string.Empty;

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
    public string AadharNumber { get; set; } = string.Empty;

    [MaxLength(10)]
    public string PanCard { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? GSTNumber { get; set; }

    [MaxLength(50)]
    public string? ReraRegistrationNumber { get; set; }

    public string? ProfilePhotoUrl { get; set; }

    public bool IsMobileVerified { get; set; } = false;

    public bool IsEmailVerified { get; set; } = false;

    public int Credits { get; set; } = 0;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
}
