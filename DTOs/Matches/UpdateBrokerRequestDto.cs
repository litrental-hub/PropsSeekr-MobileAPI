using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class UpdateBrokerRequestDto
{
    public string? Name { get; set; }
    public string? Phone { get; set; }

    [JsonPropertyName("mobile_number")]
    public string? MobileNumber { get; set; }

    public string? Locality { get; set; }
    public string? BrokerageName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("companyGst")]
    public string? CompanyGst { get; set; }

    [JsonPropertyName("companyAddress")]
    public string? CompanyAddress { get; set; }

    [JsonPropertyName("profilePhotoUrl")]
    public string? ProfilePhotoUrl { get; set; }
}
