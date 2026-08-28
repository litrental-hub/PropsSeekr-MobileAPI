using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Search;

public class PropertySearchResultItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("listingType")]
    public string ListingType { get; set; } = "SUPPLY";

    [JsonPropertyName("transactionType")]
    public string TransactionType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("propertyType")]
    public string? PropertyType { get; set; }

    [JsonPropertyName("bhk")]
    public string? Bhk { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("priceUnit")]
    public string? PriceUnit { get; set; }

    [JsonPropertyName("builtUpSize")]
    public decimal? BuiltUpSize { get; set; }

    [JsonPropertyName("availableFrom")]
    public string? AvailableFrom { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("lastRefreshedAt")]
    public DateTime? LastRefreshedAt { get; set; }

    [JsonPropertyName("freshnessCategory")]
    public string? FreshnessCategory { get; set; }

    [JsonPropertyName("unlockCost")]
    public int? UnlockCost { get; set; }

    [JsonPropertyName("isNearby")]
    public bool IsNearby { get; set; }

    [JsonPropertyName("distanceKm")]
    public double? DistanceKm { get; set; }

    [JsonPropertyName("locationLabel")]
    public string? LocationLabel { get; set; }

    [JsonPropertyName("locality")]
    public string? Locality { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("furnishing")]
    public string? Furnishing { get; set; }

    [JsonPropertyName("facing")]
    public string? Facing { get; set; }

    [JsonPropertyName("floorNumber")]
    public int? FloorNumber { get; set; }

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("roadInfo")]
    public string? RoadInfo { get; set; }

    [JsonPropertyName("features")]
    public List<FeatureItemDto> Features { get; set; } = new();

    [JsonPropertyName("preferences")]
    public List<PreferenceItemDto> Preferences { get; set; } = new();
}

public class FeatureItemDto
{
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public class PreferenceItemDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }
}
