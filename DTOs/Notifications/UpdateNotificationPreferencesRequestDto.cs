using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Notifications;

public class UpdateNotificationPreferencesRequestDto
{
    [JsonPropertyName("whatsapp_enabled")]
    public bool? WhatsappEnabled { get; set; }

    [JsonPropertyName("in_app_enabled")]
    public bool? InAppEnabled { get; set; }

    [JsonPropertyName("reminder_frequency_cap_hours")]
    public int? ReminderFrequencyCapHours { get; set; }
}
