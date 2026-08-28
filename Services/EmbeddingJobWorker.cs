using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class EmbeddingJobWorker(IServiceScopeFactory scopeFactory, ILogger<EmbeddingJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                if (!processed) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedding job worker loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        await RecoverExpiredLeasesAsync(db, now, cancellationToken);
        var job = await db.EmbeddingJobs.AsNoTracking()
            .Where(item => item.Status == "queued" && item.AvailableAt <= now)
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return false;

        var claimed = await db.EmbeddingJobs.Where(item =>
                item.Id == job.Id && item.Status == "queued" && item.AvailableAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "processing")
                .SetProperty(item => item.LockedAt, now)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return true;

        try
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IMatchingPipelineService>();
            if (job.EntityType == "listing")
            {
                await ResetListingEmbeddingAsync(db, job.EntityId, cancellationToken);
                await pipeline.TriggerForListingAsync(job.EntityId, cancellationToken);
            }
            else if (job.EntityType == "requirement")
            {
                await ResetRequirementEmbeddingAsync(db, job.EntityId, cancellationToken);
                await pipeline.TriggerForRequirementAsync(job.EntityId, cancellationToken);
            }
            else throw new InvalidOperationException($"Unsupported embedding job type '{job.EntityType}'.");

            await db.EmbeddingJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockedAt == now)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "completed")
                .SetProperty(item => item.CompletedAt, DateTime.UtcNow)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
        }
        catch (Exception ex)
        {
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var errorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            await db.EmbeddingJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockedAt == now)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AttemptCount, attempts)
                .SetProperty(item => item.Status, terminal ? "failed" : "queued")
                .SetProperty(item => item.AvailableAt, DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, attempts))))
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.LastError, errorMessage)
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            logger.LogWarning(ex, "Embedding job {JobId} attempt {Attempt} failed.", job.Id, attempts);
        }
        return true;
    }

    private async Task RecoverExpiredLeasesAsync(AppDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - LeaseDuration;
        var expiredJobs = await db.EmbeddingJobs.AsNoTracking()
            .Where(item => item.Status == "processing" && item.LockedAt != null && item.LockedAt < cutoff)
            .Select(item => new { item.Id, item.LockedAt, item.AttemptCount, item.MaxAttempts })
            .ToListAsync(cancellationToken);

        foreach (var job in expiredJobs)
        {
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var recovered = await db.EmbeddingJobs.Where(item =>
                    item.Id == job.Id && item.Status == "processing" && item.LockedAt == job.LockedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AttemptCount, attempts)
                    .SetProperty(item => item.Status, terminal ? "failed" : "queued")
                    .SetProperty(item => item.AvailableAt, item => terminal ? item.AvailableAt : now)
                    .SetProperty(item => item.LockedAt, (DateTime?)null)
                    .SetProperty(item => item.LastError, "Worker lease expired before the embedding job completed.")
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (recovered > 0)
                logger.LogWarning("Recovered expired lease for embedding job {JobId}; attempt {Attempt}.", job.Id, attempts);
        }
    }

    private static Task<int> ResetListingEmbeddingAsync(AppDbContext db, int listingId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE listings SET embedding = NULL, embedding_model = NULL WHERE listingid = {listingId}", cancellationToken);

    private static Task<int> ResetRequirementEmbeddingAsync(AppDbContext db, int requirementId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE requirements SET embedding = NULL, embedding_model = NULL WHERE requirementid = {requirementId}", cancellationToken);
}
