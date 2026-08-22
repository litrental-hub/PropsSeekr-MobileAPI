using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("notification_preferences")]
public class NotificationPreference
{
    [Key]
    public int Id { get; set; }

    [Column("broker_id")]
    public int BrokerId { get; set; }
    [ForeignKey("BrokerId")]
    public Broker? Broker { get; set; }

    [Column("in_app_enabled")]
    public bool InAppEnabled { get; set; } = true;

    [Column("whatsapp_enabled")]
    public bool WhatsappEnabled { get; set; } = true;

    [Column("reminder_frequency_cap_hours")]
    public int ReminderFrequencyCapHours { get; set; } = 4;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
