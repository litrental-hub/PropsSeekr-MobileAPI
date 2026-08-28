using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Search;

public class RequirementSearchResultItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("listingType")]
    public string ListingType { get; set; } = "DEMAND";

    [JsonPropertyName("transactionType")]
    public string TransactionType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("sub")]
    public string? Sub { get; set; }

    [JsonPropertyName("propertyType")]
    public string? PropertyType { get; set; }

    [JsonPropertyName("configurations")]
    public string[] Configurations { get; set; } = [];

    [JsonPropertyName("budget")]
    public decimal? Budget { get; set; }

    [JsonPropertyName("budgetUnit")]
    public string? BudgetUnit { get; set; }

    [JsonPropertyName("requiredSize")]
    public decimal? RequiredSize { get; set; }

    [JsonPropertyName("furnishingPreference")]
    public string? FurnishingPreference { get; set; }

    [JsonPropertyName("facingPreference")]
    public string? FacingPreference { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("locality")]
    public string? Locality { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("distanceKm")]
    public double? DistanceKm { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("lastRefreshedAt")]
    public DateTime? LastRefreshedAt { get; set; }

    [JsonPropertyName("freshnessCategory")]
    public string? FreshnessCategory { get; set; }

}
