using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;

namespace PropSeekr.Services.Interfaces;

public interface IRequirementService
{
    Task<MyRequirementsResponseDto> GetMyRequirementsAsync(
        Guid userId,
        PaginationDto pagination,
        string? transactionType = null);
    Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request);
}
