using System;

namespace PropSeekr.DTOs.Profile;

public class ProfileResponseDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string MobileNumber { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? ProfilePhotoUrl { get; set; }

    public int RemainingCreditBalance { get; set; }

    public bool IsEmailVerified { get; set; }

    public bool IsMobileVerified { get; set; }

    public int? BrokerId { get; set; }
}
