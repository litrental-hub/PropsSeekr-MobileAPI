using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class Reveal
{
    public int Id { get; set; }

    public int MatchId { get; set; }
    public Match? Match { get; set; }

    public long? ConnectionRequestId { get; set; }
    public MatchConnectionRequest? ConnectionRequest { get; set; }

    public DateTime RevealedAt { get; set; }
}
