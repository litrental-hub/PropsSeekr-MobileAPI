using PropSeekr.DTOs.Inventory;
using PropSeekr.DTOs.Requirements;

namespace PropSeekr.Services.Interfaces;

public interface IBrokerInventoryService
{
    Task<GetMyPropertyListingsResponseDto> GetMyListingsWithMatchesAsync(Guid userId, int page, int limit);
    Task<MyRequirementsResponseDto> GetMyRequirementsWithMatchesAsync(Guid userId, int page, int limit);
}
