using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class SendOtpRequestDto
{
    public bool SupportsWidget { get; set; }
    [Required]
    [RegularExpression(@"^(?:\+?91)?[0-9]{10}$",
        ErrorMessage = "Enter a 10-digit Indian mobile number, optionally prefixed with +91")]
    public string MobileNumber { get; set; } = string.Empty;
}
