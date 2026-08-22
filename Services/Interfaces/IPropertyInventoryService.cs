using PropSeekr.DTOs.Inventory;

namespace PropSeekr.Services.Interfaces;

public interface IPropertyInventoryService
{
    Task<GetMyPropertyListingsResponseDto> GetMyPropertyListingsAsync(Guid userId, int page, int limit);
    Task<PropertyListingDto> CreatePropertyListingAsync(Guid userId, CreatePropertyListingRequestDto request);
    Task<MyPropertiesResponseDto> GetMyPropertiesWithMetricsAsync(Guid userId, string? status, int page, int limit);
    Task<AddPropertyResponseDto> AddPropertyAsync(AddPropertyRequestDto request);
    Task<bool> UpdatePropertyStatusAsync(Guid id, Guid userId, string status);
}
