using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class CreditPack
{
    public int Id { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public int Credits { get; set; }

    public decimal Price { get; set; }

    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
