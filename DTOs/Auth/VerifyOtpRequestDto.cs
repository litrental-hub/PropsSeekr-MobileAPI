using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class VerifyOtpRequestDto
{
    [Required]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Mobile number must be 10 digits")]
    public string Mobile { get; set; } = string.Empty;

    [Required]
    [MaxLength(6)]
    public string Otp { get; set; } = string.Empty;
}
