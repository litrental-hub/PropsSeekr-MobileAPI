using PropSeekr.DTOs.Search;

namespace PropSeekr.Services.Interfaces;

public interface ISearchPropertyService
{
    Task<SearchPropertyResponseDto> SearchPropertiesAsync(SearchPropertyRequestDto request, Guid userId);
}
