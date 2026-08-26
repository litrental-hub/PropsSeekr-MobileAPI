namespace PropSeekr.Services.Interfaces;

public interface IMatchingPipelineService
{
    Task TriggerForListingAsync(int listingId, CancellationToken cancellationToken = default);
    Task TriggerForRequirementAsync(int requirementId, CancellationToken cancellationToken = default);
}
