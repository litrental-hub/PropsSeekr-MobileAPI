namespace PropSeekr.DTOs.Inventory;

public sealed class GetBrokerListingsResponseDto
{
    public bool Success { get; set; } = true;
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public List<BrokerListingDto> Data { get; set; } = new();
}
