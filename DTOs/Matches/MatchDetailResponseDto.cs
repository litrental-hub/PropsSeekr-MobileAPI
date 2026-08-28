using System.Text.Json;

namespace PropSeekr.DTOs.Matches;

public sealed class MatchDetailResponseDto
{
    public bool Success { get; set; } = true;
    public MatchDetailDto Data { get; set; } = new();
}

public sealed class MatchDetailDto
{
    public int MatchId { get; set; }
    public int ListingId { get; set; }
    public int RequirementId { get; set; }
    public decimal? MatchScore { get; set; }
    public string State { get; set; } = "matched";
    public string CurrentBrokerRole { get; set; } = string.Empty;
    public bool CurrentBrokerConfirmed { get; set; }
    public bool IsRevealed { get; set; }
    public string? ConnectionRequestStatus { get; set; }
    public ContactDetailsDto? UnlockedContact { get; set; }
    public ListingMatchDetailDto Property { get; set; } = new();
    public RequirementMatchDetailDto Requirement { get; set; } = new();
}

public sealed class ListingMatchDetailDto
{
    public int ListingId { get; set; }
    public string? TransactionType { get; set; }
    public string? PropertyType { get; set; }
    public string? Configuration { get; set; }
    public decimal? Price { get; set; }
    public string? PriceUnit { get; set; }
    public decimal? Size { get; set; }
    public IReadOnlyList<ListingSizeDetailDto> Sizes { get; set; } = [];
    public string? Furnishing { get; set; }
    public string? Facing { get; set; }
    public int? FloorNumber { get; set; }
    public string? Status { get; set; }
    public string? ProjectName { get; set; }
    public string? Locality { get; set; }
    public string? RoadInfo { get; set; }
    public string? City { get; set; }
    public string? Description { get; set; }
    public string? PhotoSharingPreference { get; set; }
    public JsonElement Details { get; set; }
    public IReadOnlyList<ListingMediaDto> Media { get; set; } = [];
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class ListingSizeDetailDto
{
    public decimal SizeSqft { get; set; }
    public string? Label { get; set; }
}

public sealed class ListingMediaDto
{
    public long MediaId { get; set; }
    public string MediaType { get; set; } = "image";
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int SortOrder { get; set; }
}

public sealed class RequirementMatchDetailDto
{
    public int RequirementId { get; set; }
    public string? TransactionType { get; set; }
    public string? PropertyType { get; set; }
    public IReadOnlyList<string> Configurations { get; set; } = [];
    public decimal? Budget { get; set; }
    public decimal? BudgetMin { get; set; }
    public string? BudgetType { get; set; }
    public string? BudgetUnit { get; set; }
    public decimal? Size { get; set; }
    public decimal? SizeMax { get; set; }
    public double? RadiusKm { get; set; }
    public IReadOnlyList<string> PreferredProjectNames { get; set; } = [];
    public string? FurnishingPreference { get; set; }
    public string? FacingPreference { get; set; }
    public string? Status { get; set; }
    public string? City { get; set; }
    public string? Description { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
