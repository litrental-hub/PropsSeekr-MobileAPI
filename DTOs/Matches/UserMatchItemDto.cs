namespace PropSeekr.DTOs.Matches;

public class ContactDetailsDto
{
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerMobile { get; set; } = string.Empty;
    public string? OwnerEmail { get; set; }
}

public class UserMatchItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
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
    
    // Only populated when IsUnlocked == true (Security enforcement)
    public ContactDetailsDto? OwnerContact { get; set; }
}
