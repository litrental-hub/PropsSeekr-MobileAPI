using PropSeekr.DTOs.Inventory;

namespace PropSeekr.Services.Interfaces;

public interface IPropertyInventoryService
{
    Task<GetMyPropertyListingsResponseDto> GetMyPropertyListingsAsync(Guid userId, int page, int limit);
    Task<PropertyListingDto> CreatePropertyListingAsync(Guid userId, CreatePropertyListingRequestDto request);
}
