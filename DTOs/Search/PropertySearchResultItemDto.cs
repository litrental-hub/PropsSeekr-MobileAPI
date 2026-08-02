using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Search;

public class PropertySearchResultItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string Subtitle { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("propertyType")]
    public string PropertyType { get; set; } = string.Empty;

    [JsonPropertyName("bhk")]
    public string Bhk { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public long Price { get; set; }

    [JsonPropertyName("builtUpSize")]
    public long BuiltUpSize { get; set; }

    [JsonPropertyName("availableFrom")]
    public string AvailableFrom { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("unlockCost")]
    public int UnlockCost { get; set; } = 1;

    [JsonPropertyName("isNearby")]
    public bool IsNearby { get; set; }

    [JsonPropertyName("locationLabel")]
    public string LocationLabel { get; set; } = string.Empty;

    [JsonPropertyName("brokerName")]
    public string BrokerName { get; set; } = string.Empty;

    [JsonPropertyName("brokerInitials")]
    public string BrokerInitials { get; set; } = string.Empty;

    [JsonPropertyName("brokerSub")]
    public string BrokerSub { get; set; } = string.Empty;

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
