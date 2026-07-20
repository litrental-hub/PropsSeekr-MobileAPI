using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Payment;

public class CreateOrderRequestDto
{
    [Required]
    [MaxLength(50)]
    public string TierId { get; set; } = string.Empty; // e.g., CREDITS_10, CREDITS_20, CREDITS_50
}
