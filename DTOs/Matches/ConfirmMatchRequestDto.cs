using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class ConfirmMatchRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("availability_confirmed")]
    public bool AvailabilityConfirmed { get; set; }

    [JsonPropertyName("price_valid")]
    public bool PriceValid { get; set; }

    [JsonPropertyName("price_negotiable")]
    public bool PriceNegotiable { get; set; }

    [JsonPropertyName("ready_to_connect")]
    public bool ReadyToConnect { get; set; }
}
