using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class EmbeddingJobService(AppDbContext dbContext) : IEmbeddingJobService
{
    public async Task<EmbeddingJob> EnqueueAsync(string entityType, int entityId, CancellationToken cancellationToken = default)
    {
        entityType = entityType.Trim().ToLowerInvariant();
        if (entityType is not ("listing" or "requirement"))
            throw new ArgumentException("Embedding jobs support only listing or requirement entities.", nameof(entityType));
        if (entityId <= 0)
            throw new ArgumentOutOfRangeException(nameof(entityId));

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // PostgreSQL transaction advisory locks serialize enqueue decisions for
        // one canonical entity without preventing unrelated jobs from queuing.
        var lockKey = $"propseekr-embedding-job:{entityType}:{entityId}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", cancellationToken);

        // A queued job will read the latest persisted content. A processing job
        // may already hold old content, so an edit must create a successor job.
        var existing = await dbContext.EmbeddingJobs.FirstOrDefaultAsync(job =>
            job.EntityType == entityType && job.EntityId == entityId && job.Status == "queued", cancellationToken);
        if (existing is not null)
        {
            if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
            return existing;
        }

        var job = new EmbeddingJob { EntityType = entityType, EntityId = entityId };
        dbContext.EmbeddingJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<EmbeddingJob> RetryFailedAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.EmbeddingJobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
                  ?? throw new KeyNotFoundException("Embedding job not found.");
        if (job.Status != "failed")
            throw new InvalidOperationException("Only failed embedding jobs can be retried.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"propseekr-embedding-job:{job.EntityType}:{job.EntityId}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", cancellationToken);

        var existingQueuedJob = await dbContext.EmbeddingJobs.AnyAsync(item =>
            item.Id != job.Id && item.EntityType == job.EntityType && item.EntityId == job.EntityId && item.Status == "queued", cancellationToken);
        if (existingQueuedJob)
            throw new InvalidOperationException("A newer embedding job is already queued for this record.");

        job.Status = "queued";
        job.AttemptCount = 0;
        job.AvailableAt = DateTime.UtcNow;
        job.LockedAt = null;
        job.CompletedAt = null;
        job.LastError = null;
        job.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }
}
