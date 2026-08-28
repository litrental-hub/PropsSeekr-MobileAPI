using PropSeekr.DTOs.Inventory;

namespace PropSeekr.Services.Interfaces;

public interface IBrokerListingsService
{
    Task<GetBrokerListingsResponseDto> GetAllListingsAsync(
        int page,
        int limit,
        string? transactionType = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<GetBrokerListingsResponseDto> GetMyListingsAsync(
        int brokerId,
        int page,
        int limit,
        string? transactionType = null,
        string? status = null,
        CancellationToken cancellationToken = default);
}
