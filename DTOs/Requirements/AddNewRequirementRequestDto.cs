using System;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Requirements;

public class AddNewRequirementRequestDto
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("lookingFor")]
    public string LookingFor { get; set; } = string.Empty;

    [JsonPropertyName("listingType")]
    public string ListingType { get; set; } = string.Empty;

    [JsonPropertyName("propertyType")]
    public string PropertyType { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("radiusKm")]
    public double RadiusKm { get; set; }

    [JsonPropertyName("budget")]
    public string Budget { get; set; } = string.Empty;

    [JsonPropertyName("minBudgetNumeric")]
    public long MinBudgetNumeric { get; set; }

    [JsonPropertyName("maxBudgetNumeric")]
    public long MaxBudgetNumeric { get; set; }

    [JsonPropertyName("clientNotes")]
    public string ClientNotes { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string? City { get; set; }
}
