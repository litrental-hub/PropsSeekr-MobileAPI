namespace PropSeekr.DTOs.Search;

public class SearchPropertyResponseDto
{
    public bool Success { get; set; } = true;
    public MetadataDto Metadata { get; set; } = new();
    public List<PropertySearchResponseItemDto> Data { get; set; } = new();
}
