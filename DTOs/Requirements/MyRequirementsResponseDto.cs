using PropSeekr.DTOs.Search;

namespace PropSeekr.DTOs.Requirements;

public class MyRequirementsResponseDto
{
    public bool Success { get; set; } = true;
    public MetadataDto Metadata { get; set; } = new();
    public List<RequirementListItemDto> Data { get; set; } = new();
}
