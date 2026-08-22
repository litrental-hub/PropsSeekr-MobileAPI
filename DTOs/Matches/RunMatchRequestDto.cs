using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class RunMatchRequestDto
{
    [JsonPropertyName("listing_id")]
    public int? ListingId { get; set; }

    [JsonPropertyName("requirement_id")]
    public int? RequirementId { get; set; }
}
