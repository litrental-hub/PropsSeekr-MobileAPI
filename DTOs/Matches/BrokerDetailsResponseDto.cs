using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class BrokerDetailsResponseDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("locality")]
    public string Locality { get; set; } = string.Empty;

    [JsonPropertyName("brokerage_name")]
    public string BrokerageName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("response_score")]
    public decimal ResponseScore { get; set; }

    [JsonPropertyName("confirmation_compliance_rate")]
    public decimal ConfirmationComplianceRate { get; set; }

    [JsonPropertyName("visibility_penalty_flag")]
    public bool VisibilityPenaltyFlag { get; set; }

    [JsonPropertyName("free_credits_balance")]
    public int FreeCreditsBalance { get; set; }

    [JsonPropertyName("paid_credits_balance")]
    public int PaidCreditsBalance { get; set; }

    // Additional fields mapped from the User entity
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("companyGst")]
    public string? CompanyGst { get; set; }

    [JsonPropertyName("companyAddress")]
    public string? CompanyAddress { get; set; }

    [JsonPropertyName("profilePhotoUrl")]
    public string? ProfilePhotoUrl { get; set; }
}
