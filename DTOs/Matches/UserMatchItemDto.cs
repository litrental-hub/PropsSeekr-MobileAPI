using System;

namespace PropSeekr.DTOs.Matches;

public class ContactDetailsDto
{
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerMobile { get; set; } = string.Empty;
    public string? OwnerEmail { get; set; }
}

public class PropertyMatchSideDto
{
    public string CategoryHeader { get; set; } = string.Empty;
    public string DetailsLine { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string PriceLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class RequirementMatchSideDto
{
    public string CategoryHeader { get; set; } = string.Empty;
    public string DetailsLine { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string PriceLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class UserMatchItemDto
{
    // Match ID for unlock operations - replaces PropertyRequestId
    public int MatchId { get; set; }
    
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public long? BudgetMin { get; set; }
    public long? BudgetMax { get; set; }
    public DateTime PostedAt { get; set; }
    public string PostedTimeAgo { get; set; } = string.Empty;
    public int MatchScore { get; set; }
    public bool IsUnlocked { get; set; }
    public string UnlockStatus { get; set; } = "NONE"; // NONE, PENDING, REQUESTED, UNLOCKED, CONFIRMED
    
    // Only populated when IsUnlocked == true (Security enforcement)
    public ContactDetailsDto? OwnerContact { get; set; }

    // Helper IDs for unlocking
    public Guid? PropertyRequestId { get; set; }
    public Guid? InitiatorPropertyRequestId { get; set; }

    // New matching side metadata for UI rendering
    public PropertyMatchSideDto Property { get; set; } = new();
    public RequirementMatchSideDto Requirement { get; set; } = new();

    public double? DistanceKm { get; set; }
    public string DistanceLabel { get; set; } = string.Empty;
}
