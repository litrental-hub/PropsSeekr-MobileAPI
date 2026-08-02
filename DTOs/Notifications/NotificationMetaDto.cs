using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Notifications;

public class NotificationMetaDto
{
    [JsonPropertyName("brokerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrokerId { get; set; }

    [JsonPropertyName("brokerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrokerName { get; set; }

    [JsonPropertyName("brokerPhone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BrokerPhone { get; set; }

    [JsonPropertyName("propertyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertyId { get; set; }

    [JsonPropertyName("propertyTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertyTitle { get; set; }

    [JsonPropertyName("matchId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchId { get; set; }

    [JsonPropertyName("matchPercentage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MatchPercentage { get; set; }
}
