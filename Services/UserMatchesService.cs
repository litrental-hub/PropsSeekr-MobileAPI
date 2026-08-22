using PropSeekr.DTOs.Matches;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>Unlocking is match-centric; the UI must submit matches.matchid.</summary>
public class UserMatchesService : IUserMatchesService
{
    private readonly IUnlockService _unlockService;
    public UserMatchesService(IUnlockService unlockService) => _unlockService = unlockService;
    public Task<UnlockPropertyResponseDto> UnlockPropertyAsync(Guid userId, UnlockPropertyRequestDto request) => _unlockService.UnlockMatchAsync(userId, request);
    public Task<UserMatchesResponseDto> GetUserMatchesAsync(Guid userId, string? transactionType = null) => Task.FromResult(new UserMatchesResponseDto());
    public Task<UserMatchesResponseDto> GetUnlockedPropertiesAsync(Guid userId) => Task.FromResult(new UserMatchesResponseDto());
}
