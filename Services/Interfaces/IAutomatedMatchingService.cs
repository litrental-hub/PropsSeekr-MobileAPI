namespace PropSeekr.Services.Interfaces;

public interface IAutomatedMatchingService
{
    Task<IReadOnlyList<int>> RunForListingAsync(int listingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> RunForRequirementAsync(int requirementId, CancellationToken cancellationToken = default);
}
