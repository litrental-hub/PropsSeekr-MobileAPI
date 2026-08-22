using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PropSeekr.DTOs.Matches;

public class CreateRequirementRequestDto
{
    [JsonPropertyName("broker_id")]
    public int BrokerId { get; set; }

    [JsonPropertyName("requirement_type")]
    public string RequirementType { get; set; } = string.Empty;

    [JsonPropertyName("property_type")]
    public string? PropertyType { get; set; }

    [JsonPropertyName("budget")]
    public decimal? Budget { get; set; }

    [JsonPropertyName("budget_unit")]
    public string? BudgetUnit { get; set; }

    [JsonPropertyName("size")]
    public decimal? Size { get; set; }

    [JsonPropertyName("locality_ids")]
    public List<int>? LocalityIds { get; set; }

    [JsonPropertyName("configurations")]
    public List<string>? Configurations { get; set; }

    [JsonPropertyName("raw_message_text")]
    public string? RawMessageText { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("furnishing_pref")]
    public string? FurnishingPref { get; set; }

    [JsonPropertyName("facing_pref")]
    public string? FacingPref { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    [JsonPropertyName("message_datetime")]
    public DateTime? MessageDatetime { get; set; }

    [JsonPropertyName("budget_type")]
    public string? BudgetType { get; set; }

    [JsonPropertyName("isavailable")]
    public bool? IsAvailable { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("listing_ids")]
    public List<int>? ListingIds { get; set; }

    [JsonPropertyName("posted_by")]
    public string? PostedBy { get; set; }
}
