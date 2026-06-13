using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Profile;

public class UpdateProfileRequestDto
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(20)]
    public string? GstNumber { get; set; }

    [MaxLength(50)]
    public string? ReraRegistrationNumber { get; set; }
}
