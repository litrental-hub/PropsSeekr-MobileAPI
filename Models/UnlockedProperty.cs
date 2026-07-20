using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class UnlockedProperty
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid PropertyRequestId { get; set; }
    public PropertyRequest? PropertyRequest { get; set; }

    public DateTime UnlockedAt { get; set; } = DateTime.UtcNow;
}
