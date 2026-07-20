using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NetTopologySuite.Geometries;

namespace PropSeekr.Models;

public class PropertyRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(50)]
    public string Status { get; set; } = "LOOKING"; // LOOKING, ACTIVE, etc.

    [MaxLength(50)]
    public string ListingType { get; set; } = string.Empty; // SUPPLY or DEMAND

    [MaxLength(50)]
    public string TransactionType { get; set; } = string.Empty; // BUY_SELL or RENTAL

    [MaxLength(50)]
    public string Category { get; set; } = string.Empty; // RESIDENTIAL, COMMERCIAL, etc.

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    // JSON stored fields for complex objects
    public string PreferredLocationsJson { get; set; } = "[]";
    public string BudgetJson { get; set; } = "{}";
    public string RequiredAreaJson { get; set; } = "{}";
    public string UrgencyJson { get; set; } = "{}";
    public string ClientPreferencesJson { get; set; } = "[]";
    public string FiltersJson { get; set; } = "{}";
    public string SearchQueryJson { get; set; } = "{}";
    
    // Denormalized columns for filtering
    public long? BudgetMin { get; set; }
    public long? BudgetMax { get; set; }

    // Property types stored as JSON/text (e.g. ["2BHK","3BHK"]) to allow filtering
    public string PropertyTypesJson { get; set; } = "[]";

    // Location fields for filtering
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Locality { get; set; } = string.Empty;

    /// <summary>
    /// PostGIS geography point (SRID 4326, WGS84).
    /// Stored as Point(Longitude, Latitude) — NTS convention.
    /// Used for spatial distance queries via ST_DWithin / IsWithinDistance.
    /// </summary>
    public Point? Location { get; set; }

    // Timestamps
    public DateTime PostedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    // Navigation property
    public User? User { get; set; }
}
