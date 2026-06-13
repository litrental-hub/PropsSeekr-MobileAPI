using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class SendOtpRequestDto
{
    [Required]
    [RegularExpression(@"^\d{10}$",
        ErrorMessage = "Mobile number must be 10 digits")]
    public string MobileNumber { get; set; } = string.Empty;
}
