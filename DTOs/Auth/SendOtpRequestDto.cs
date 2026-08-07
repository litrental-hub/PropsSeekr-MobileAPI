using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class SendOtpRequestDto
{
    [Required]
    [RegularExpression(@"^(\+91|91)?\d{10}$",
        ErrorMessage = "Mobile number must be 10 digits, optionally prefixed with 91 or +91")]
    public string MobileNumber { get; set; } = string.Empty;
}
