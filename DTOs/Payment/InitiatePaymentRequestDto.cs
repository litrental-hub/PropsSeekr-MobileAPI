using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Payment;

public class InitiatePaymentRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("credit_pack_id")]
    public int CreditPackId { get; set; }
}
