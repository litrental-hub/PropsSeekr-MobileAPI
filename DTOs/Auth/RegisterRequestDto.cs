using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class RegisterRequestDto
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Mobile number is required")]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Mobile number must be 10 digits")]
    public string Mobile { get; set; } = string.Empty;

    [Required]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long")]
    public string Password { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{12}$", ErrorMessage = "Aadhar number must be 12 digits")]
    public string AadharNumber { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^[A-Z]{5}[0-9]{4}[A-Z]{1}$", ErrorMessage = "Invalid PAN card format")]
    public string PanCard { get; set; } = string.Empty;

    public string? GstNumber { get; set; }

    public string? ReraRegistrationNumber { get; set; }
}