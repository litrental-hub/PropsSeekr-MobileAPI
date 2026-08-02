using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Notifications;

public class NotificationListResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }

    [JsonPropertyName("data")]
    public List<NotificationResponseDto> Data { get; set; } = new();
}
