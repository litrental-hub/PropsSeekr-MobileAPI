using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class CreditPack
{
    public int Id { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(50)]
    public string Code { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    [StringLength(10)]
    public string Currency { get; set; } = "INR";

    public long AmountInPaise { get; set; }

    public int Credits { get; set; }

    public decimal Price { get; set; }

    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;

    public DateTime? EffectiveTo { get; set; }
}
