using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;

namespace PropSeekr.Services.Interfaces;

public interface IRequirementService
{
    Task<MyRequirementsResponseDto> GetMyRequirementsAsync(Guid userId, PaginationDto pagination);
    Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request);
    Task<MyRequirementsListResponseDto> GetMyRequirementsWithMetricsAsync(Guid userId, string? status, int page, int limit);
    Task<AddRequirementResponseDto> AddRequirementAsync(AddRequirementRequestDto request);
}
