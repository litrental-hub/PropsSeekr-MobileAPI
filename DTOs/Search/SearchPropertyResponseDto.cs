using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Search;

public class SearchPropertyResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";

    [JsonPropertyName("availableCount")]
    public int AvailableCount { get; set; }

    [JsonPropertyName("lookingCount")]
    public int LookingCount { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("results")]
    public List<PropertySearchResultItemDto> Results { get; set; } = new();

    [JsonPropertyName("requirements")]
    public List<RequirementSearchResultItemDto> Requirements { get; set; } = new();
}
// Keep a compatibility property inside the namespace or assembly if needed, but not necessary since we refactor the service and controller to return the new DTO structure cleanly.
