using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;

namespace PropSeekr.Services.Interfaces;

public interface IRequirementService
{
    Task<MyRequirementsResponseDto> GetMyRequirementsAsync(Guid userId, PaginationDto pagination);
    Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request);
}
