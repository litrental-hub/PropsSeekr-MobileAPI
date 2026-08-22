using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class RegisterBrokerResponseDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("free_credits_balance")]
    public int FreeCreditsBalance { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";
}
