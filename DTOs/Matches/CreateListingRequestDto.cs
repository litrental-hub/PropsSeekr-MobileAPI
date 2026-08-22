using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class CreateListingRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("master_id")]
    public int? MasterId { get; set; }

    [JsonPropertyName("property_type")]
    public string? PropertyType { get; set; }

    [JsonPropertyName("locality")]
    public string? Locality { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("sizes")]
    public List<ListingSizeDto>? Sizes { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("raw_message_text")]
    public string? RawMessageText { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("listing_type")]
    public string? ListingType { get; set; }

    [JsonPropertyName("configuration")]
    public string? Configuration { get; set; }

    [JsonPropertyName("price_unit")]
    public string? PriceUnit { get; set; }

    [JsonPropertyName("size")]
    public decimal? Size { get; set; }

    [JsonPropertyName("furnishing")]
    public string? Furnishing { get; set; }

    [JsonPropertyName("facing")]
    public string? Facing { get; set; }

    [JsonPropertyName("project_name")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("road_info")]
    public string? RoadInfo { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    [JsonPropertyName("message_datetime")]
    public DateTime? MessageDatetime { get; set; }

    [JsonPropertyName("price_status")]
    public string? PriceStatus { get; set; }

    [JsonPropertyName("isavailable")]
    public bool? IsAvailable { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("requirement_ids")]
    public List<int>? RequirementIds { get; set; }

    [JsonPropertyName("posted_by")]
    public string? PostedBy { get; set; }
}

public class ListingSizeDto
{
    [JsonPropertyName("size_sqft")]
    public decimal SizeSqft { get; set; }

    [JsonPropertyName("bhk")]
    public int? Bhk { get; set; }
}
