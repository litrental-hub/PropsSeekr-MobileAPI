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

    [JsonPropertyName("initiatorUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiatorUserId { get; set; }

    [JsonPropertyName("initiatorPropertyRequestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitiatorPropertyRequestId { get; set; }

    [JsonPropertyName("targetPropertyRequestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPropertyRequestId { get; set; }
}
