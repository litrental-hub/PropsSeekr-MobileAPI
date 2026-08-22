using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

public class Notification
{
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [ForeignKey("UserId")]
    public User? User { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty; // e.g. "BROKER_UNLOCK", "MATCH", "BROKER_REQUEST"

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    public bool IsRead { get; set; } = false;

    public bool RequiresTokenUnlock { get; set; } = false;

    public bool IsContactUnlocked { get; set; } = false;

    public int TokenCost { get; set; } = 0;



    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? MetaJson { get; set; }
}
