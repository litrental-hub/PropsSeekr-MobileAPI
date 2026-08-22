using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Inventory;

public class AddPropertyRequestDto
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL

    [Required]
    public string PropertyType { get; set; } = string.Empty; // Flat/Apartment, etc.

    [Required]
    public string City { get; set; } = string.Empty;

    [Required]
    public string Area { get; set; } = string.Empty; // Locality

    public string? LandmarkStreet { get; set; }

    [Required]
    public int SqFeet { get; set; }

    public string? AvailableFrom { get; set; }

    // Pricing Info
    public long MonthlyRent { get; set; }
    public long SecurityDeposit { get; set; }
    public long MaintenanceCharges { get; set; }

    // Property Details
    public int FloorNumber { get; set; }
    public int TotalFloors { get; set; }
    public string FurnishingStatus { get; set; } = string.Empty;
    public int Bathrooms { get; set; }
    public int Balconies { get; set; }
    public string FacingDirection { get; set; } = string.Empty;
    public List<string> Amenities { get; set; } = new();
    public string DietPreferences { get; set; } = string.Empty;
    public string PetPolicy { get; set; } = string.Empty;
    public int MinimumLeasePeriod { get; set; }
    public string PoliceVerificationAllowed { get; set; } = "no"; // yes or no
    public List<string> Photos { get; set; } = new();

    [Required]
    public double Latitude { get; set; }

    [Required]
    public double Longitude { get; set; }
}
