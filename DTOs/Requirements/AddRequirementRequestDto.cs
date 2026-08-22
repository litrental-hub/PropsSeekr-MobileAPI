using System;
using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Requirements;

public class AddRequirementRequestDto
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string ContactNumber { get; set; } = string.Empty;

    [Required]
    public string LookingFor { get; set; } = string.Empty;

    [Required]
    public string ListingType { get; set; } = string.Empty; // BUY/SELL or RENTAL

    [Required]
    public string PropertyType { get; set; } = string.Empty;

    [Required]
    public string PreferredLocation { get; set; } = string.Empty; // Locality

    [Required]
    public string Configuration { get; set; } = string.Empty; // e.g. 2BHK, 3BHK

    public string FurnishingPreference { get; set; } = string.Empty;
    public string PreferredPreference { get; set; } = string.Empty;
    public string Facing { get; set; } = string.Empty;

    [Required]
    public int RequiredArea { get; set; } // SqFeet

    [Required]
    public string Budget { get; set; } = string.Empty; // a single string field!

    public string ProjectSocietyName { get; set; } = string.Empty;
    public string AdditionalNotes { get; set; } = string.Empty;

    [Required]
    public double Latitude { get; set; }

    [Required]
    public double Longitude { get; set; }

    [Required]
    public double RadiusKm { get; set; } = 5.0; // default 5km
}
