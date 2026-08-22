using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class Reveal
{
    public int Id { get; set; }

    public int MatchId { get; set; }
    public Match? Match { get; set; }

    public DateTime RevealedAt { get; set; }
}
