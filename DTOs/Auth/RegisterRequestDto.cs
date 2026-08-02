using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class RegisterRequestDto
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Mobile number is required")]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Mobile number must be 10 digits")]
    public string Mobile { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email address is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long")]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Address Line 1 is required")]
    [MaxLength(255)]
    public string AddressLine1 { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? AddressLine2 { get; set; }

    [Required(ErrorMessage = "City is required")]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [Required(ErrorMessage = "State is required")]
    [MaxLength(100)]
    public string State { get; set; } = string.Empty;

    [Required(ErrorMessage = "Pincode is required")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Pincode must be 6 digits")]
    [MaxLength(10)]
    public string Pincode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Aadhar number is required")]
    [RegularExpression(@"^\d{12}$", ErrorMessage = "Aadhar number must be 12 digits")]
    public string AadharNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "PAN card is required")]
    [RegularExpression(@"^[A-Z]{5}[0-9]{4}[A-Z]{1}$", ErrorMessage = "Invalid PAN card format")]
    public string PanCard { get; set; } = string.Empty;

    public string? GstNumber { get; set; }

    public string? ReraRegistrationNumber { get; set; }
}
