using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class DeductCreditsRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("operation_key")]
    public string OperationKey { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
