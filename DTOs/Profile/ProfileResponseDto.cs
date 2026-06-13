using System;

namespace PropSeekr.DTOs.Profile;

public class ProfileResponseDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string MobileNumber { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string AadharNumber { get; set; } = string.Empty;

    public string PanCard { get; set; } = string.Empty;

    public string? GSTNumber { get; set; }

    public string? ReraRegistrationNumber { get; set; }

    public string? ProfilePhotoUrl { get; set; }

    public bool IsMobileVerified { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime ModifiedDate { get; set; }
}
