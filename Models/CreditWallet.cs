using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class CreditWallet
{
    public int Id { get; set; }

    public int BrokerId { get; set; }

    public int FreeCreditsBalance { get; set; } = 0;
    public int PaidCreditsBalance { get; set; } = 0;

    public DateTime? FreeCreditsResetAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
