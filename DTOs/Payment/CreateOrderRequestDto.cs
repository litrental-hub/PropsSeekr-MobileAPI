using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Payment;

public class CreateOrderRequestDto
{
    public int? PackId { get; set; }

    // Temporary compatibility field for already-deployed clients.
    [MaxLength(50)]
    public string? TierId { get; set; }
}
