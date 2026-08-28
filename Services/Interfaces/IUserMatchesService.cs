using PropSeekr.DTOs.Matches;

namespace PropSeekr.Services.Interfaces;

public interface IUserMatchesService
{
    /// <summary>Returns every match for an authorized administrative view.</summary>
    Task<UserMatchesResponseDto> GetAllMatchesAsync(
        string? transactionType = null,
        int? listingId = null,
        int? requirementId = null,
        int? matchId = null,
        int page = 1,
        int limit = 20);

    /// <summary>
    /// Finds properties posted by OTHER users that match the logged-in user's requests.
    /// Hides owner contact info unless unlocked via credits.
    /// </summary>
    Task<UserMatchesResponseDto> GetUserMatchesAsync(
        Guid userId,
        string? transactionType = null,
        int? listingId = null,
        int? requirementId = null,
        int? matchId = null,
        int page = 1,
        int limit = 20);

    /// <summary>
    /// Spends 1 Token / Credit (valued at ₹300) to permanently unlock owner contact details for a property.
    /// </summary>
    Task<UnlockPropertyResponseDto> UnlockPropertyAsync(Guid userId, UnlockPropertyRequestDto request);

    /// <summary>
    /// Retrieves all properties previously unlocked by the logged-in user with owner contact details.
    /// </summary>
    Task<UserMatchesResponseDto> GetUnlockedPropertiesAsync(Guid userId, int page = 1, int limit = 20);
}
