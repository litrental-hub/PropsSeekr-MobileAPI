using System;
using System.Collections.Generic;

namespace PropSeekr.DTOs.Inventory;

public class MyPropertiesResponseDto
{
    public bool Success { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public List<MyPropertyItemDto> Data { get; set; } = new();
}

public class MyPropertyItemDto
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string LandmarkStreet { get; set; } = string.Empty;
    public int SqFeet { get; set; }
    public string AvailableFrom { get; set; } = string.Empty;

    // Pricing
    public long MonthlyRent { get; set; }
    public long SecurityDeposit { get; set; }
    public long MaintenanceCharges { get; set; }

    // Property details
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
    public string PoliceVerificationAllowed { get; set; } = "no";
    public List<string> Photos { get; set; } = new();

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Views { get; set; }
    public int Matches { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
