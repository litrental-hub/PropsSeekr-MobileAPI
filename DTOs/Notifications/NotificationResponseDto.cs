using System;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Notifications;

public class NotificationResponseDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("isRead")]
    public bool IsRead { get; set; }

    [JsonPropertyName("requiresTokenUnlock")]
    public bool RequiresTokenUnlock { get; set; }

    [JsonPropertyName("isContactUnlocked")]
    public bool IsContactUnlocked { get; set; }

    [JsonPropertyName("tokenCost")]
    public int TokenCost { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("meta")]
    public NotificationMetaDto? Meta { get; set; }
}
