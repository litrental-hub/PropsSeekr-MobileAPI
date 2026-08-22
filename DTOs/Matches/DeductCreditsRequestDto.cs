using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class DeductCreditsRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
