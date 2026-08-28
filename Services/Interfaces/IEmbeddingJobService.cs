using PropSeekr.Models;

namespace PropSeekr.Services.Interfaces;

public interface IEmbeddingJobService
{
    Task<EmbeddingJob> EnqueueAsync(string entityType, int entityId, CancellationToken cancellationToken = default);
}
