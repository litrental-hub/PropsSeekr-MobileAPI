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
    // Canonical matches.matchid used by confirmation and reveal operations.
    public int MatchId { get; set; }
    public int ListingId { get; set; }
    public int RequirementId { get; set; }

    public string State { get; set; } = "matched";
    public bool CurrentBrokerConfirmed { get; set; }
    public DateTime? WindowExpiresAt { get; set; }
    public bool IsRevealed { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
    public long? ConnectionRequestId { get; set; }
    public string? ConnectionRequestStatus { get; set; }
    public string? DeliveryChannel { get; set; }
    public bool IncomingConnectionRequest { get; set; }
    public string CurrentBrokerRole { get; set; } = string.Empty;

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
    
    // Legacy alias. Only populated when a reveals row exists.
    public ContactDetailsDto? OwnerContact { get; set; }

    // New matching side metadata for UI rendering
    public PropertyMatchSideDto Property { get; set; } = new();
    public RequirementMatchSideDto Requirement { get; set; } = new();
}
