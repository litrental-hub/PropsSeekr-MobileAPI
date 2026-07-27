using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class LoginRequestDto
{
    [Required]
    public string Identifier { get; set; } = string.Empty; // Mobile number or Email address

    [Required]
    public string Password { get; set; } = string.Empty;
}
