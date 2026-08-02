using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Notifications;

public class UnlockBrokerResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tokensDebited")]
    public int TokensDebited { get; set; } = 1;

    [JsonPropertyName("remainingTokens")]
    public int RemainingTokens { get; set; }

    [JsonPropertyName("isContactUnlocked")]
    public bool IsContactUnlocked { get; set; } = true;

    [JsonPropertyName("meta")]
    public NotificationMetaDto? Meta { get; set; }
}
