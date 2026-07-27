using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Inventory;

public class CreatePropertyListingRequestDto
{
    [Required(ErrorMessage = "Transaction type is required.")]
    [StringLength(50, ErrorMessage = "Transaction type cannot exceed 50 characters.")]
    public string TransactionType { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required.")]
    [StringLength(100, ErrorMessage = "Category cannot exceed 100 characters.")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Asking price is required.")]
    [Range(0.01, 1000000000000, ErrorMessage = "Asking price must be greater than zero.")]
    public decimal AskingPrice { get; set; }

    [Required(ErrorMessage = "Built-up size is required.")]
    [Range(0.01, 1000000000, ErrorMessage = "Built-up size must be greater than zero.")]
    public decimal BuiltUpSize { get; set; }

    [Required(ErrorMessage = "City is required.")]
    [StringLength(100, ErrorMessage = "City cannot exceed 100 characters.")]
    public string City { get; set; } = string.Empty;

    [Required(ErrorMessage = "Locality is required.")]
    [StringLength(100, ErrorMessage = "Locality cannot exceed 100 characters.")]
    public string Locality { get; set; } = string.Empty;

    [Required(ErrorMessage = "Latitude is required.")]
    [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
    public double Latitude { get; set; }

    [Required(ErrorMessage = "Longitude is required.")]
    [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
    public double Longitude { get; set; }

    public string Status { get; set; } = "ACTIVE";
}
