using System;
using System.Collections.Generic;

namespace PropSeekr.DTOs.Requirements;

public class MyRequirementsListResponseDto
{
    public bool Success { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public List<MyRequirementItemDto> Data { get; set; } = new();
}

public class MyRequirementItemDto
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string PreferredLocation { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
    public string FurnishingPreference { get; set; } = string.Empty;
    public string PreferredPreference { get; set; } = string.Empty;
    public string Facing { get; set; } = string.Empty;
    public int RequiredArea { get; set; }
    public string Budget { get; set; } = string.Empty;
    public string ProjectSocietyName { get; set; } = string.Empty;
    public string AdditionalNotes { get; set; } = string.Empty;

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double RadiusKm { get; set; }
    public string Status { get; set; } = string.Empty;
    public int MatchesFound { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
