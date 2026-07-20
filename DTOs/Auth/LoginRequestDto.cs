using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class LoginRequestDto
{
    [RegularExpression(@"^\d{10}$",
        ErrorMessage = "Mobile number must be 10 digits")]
    public string? MobileNumber { get; set; }

    [EmailAddress]
    public string? Email { get; set; }

    [Required]
    public string Password { get; set; } = string.Empty;
}
