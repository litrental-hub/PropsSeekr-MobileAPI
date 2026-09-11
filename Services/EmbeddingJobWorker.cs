using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class EmbeddingJobWorker(IServiceScopeFactory scopeFactory, ILogger<EmbeddingJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

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

        var lockToken = Guid.NewGuid();
        var claimed = await db.EmbeddingJobs.Where(item =>
                item.Id == job.Id && item.Status == "queued" && item.AvailableAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "processing")
                .SetProperty(item => item.LockedAt, now)
                .SetProperty(item => item.LockToken, lockToken)
                .SetProperty(item => item.HeartbeatAt, now)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return true;

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
        try
        {
            var currentVersion = await GetCurrentVersionAsync(db, job.EntityType, job.EntityId, cancellationToken);
            if (currentVersion != job.TargetVersion)
            {
                await SupersedeAsync(db, job, lockToken, currentVersion, cancellationToken);
                return true;
            }

            await SetEntityStatusAsync(db, job, "processing", cancellationToken);
            heartbeatTask = RunHeartbeatAsync(job.Id, lockToken, heartbeatCancellation.Token);
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

            heartbeatCancellation.Cancel();
            await AwaitHeartbeatShutdownAsync(heartbeatTask);
            heartbeatTask = null;

            currentVersion = await GetCurrentVersionAsync(db, job.EntityType, job.EntityId, cancellationToken);
            if (currentVersion != job.TargetVersion)
            {
                await ResetEntityEmbeddingAsync(db, job.EntityType, job.EntityId, cancellationToken);
                var invalidation = scope.ServiceProvider.GetRequiredService<MatchInvalidationService>();
                if (job.EntityType == "listing") await invalidation.InvalidateForListingAsync(job.EntityId, cancellationToken);
                else await invalidation.InvalidateForRequirementAsync(job.EntityId, cancellationToken);
                await SupersedeAsync(db, job, lockToken, currentVersion, cancellationToken);
                return true;
            }

            var entityCompleted = await CompleteEntityAsync(db, job, cancellationToken);
            if (entityCompleted == 0)
            {
                currentVersion = await GetCurrentVersionAsync(db, job.EntityType, job.EntityId, cancellationToken);
                await ResetEntityEmbeddingAsync(db, job.EntityType, job.EntityId, cancellationToken);
                var invalidation = scope.ServiceProvider.GetRequiredService<MatchInvalidationService>();
                if (job.EntityType == "listing") await invalidation.InvalidateForListingAsync(job.EntityId, cancellationToken);
                else await invalidation.InvalidateForRequirementAsync(job.EntityId, cancellationToken);
                await SupersedeAsync(db, job, lockToken, currentVersion, cancellationToken);
                return true;
            }
            await db.EmbeddingJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockToken == lockToken)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "completed")
                .SetProperty(item => item.CompletedAt, DateTime.UtcNow)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.LockToken, (Guid?)null)
                .SetProperty(item => item.HeartbeatAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
        }
        catch (Exception ex)
        {
            heartbeatCancellation.Cancel();
            await AwaitHeartbeatShutdownAsync(heartbeatTask);
            heartbeatTask = null;
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var errorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            await db.EmbeddingJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockToken == lockToken)
                .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AttemptCount, attempts)
                .SetProperty(item => item.Status, terminal ? "failed" : "queued")
                .SetProperty(item => item.AvailableAt, DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, attempts))))
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.LockToken, (Guid?)null)
                .SetProperty(item => item.HeartbeatAt, (DateTime?)null)
                .SetProperty(item => item.LastError, errorMessage)
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            await SetEntityStatusAsync(db, job, terminal ? "failed" : "queued", cancellationToken);
            logger.LogWarning(ex, "Embedding job {JobId} attempt {Attempt} failed.", job.Id, attempts);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            await AwaitHeartbeatShutdownAsync(heartbeatTask);
        }
        return true;
    }

    private async Task RecoverExpiredLeasesAsync(AppDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - LeaseDuration;
        var expiredJobs = await db.EmbeddingJobs.AsNoTracking()
            .Where(item => item.Status == "processing" &&
                (item.HeartbeatAt ?? item.LockedAt) != null && (item.HeartbeatAt ?? item.LockedAt) < cutoff)
            .Select(item => new { item.Id, item.LockToken, item.AttemptCount, item.MaxAttempts, item.EntityType, item.EntityId, item.TargetVersion })
            .ToListAsync(cancellationToken);

        foreach (var job in expiredJobs)
        {
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var recovered = await db.EmbeddingJobs.Where(item =>
                    item.Id == job.Id && item.Status == "processing" && item.LockToken == job.LockToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AttemptCount, attempts)
                    .SetProperty(item => item.Status, terminal ? "failed" : "queued")
                    .SetProperty(item => item.AvailableAt, item => terminal ? item.AvailableAt : now)
                    .SetProperty(item => item.LockedAt, (DateTime?)null)
                    .SetProperty(item => item.LockToken, (Guid?)null)
                    .SetProperty(item => item.HeartbeatAt, (DateTime?)null)
                    .SetProperty(item => item.LastError, "Worker lease expired before the embedding job completed.")
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (recovered > 0)
            {
                await SetEntityStatusAsync(db, new Models.EmbeddingJob
                {
                    EntityType = job.EntityType,
                    EntityId = job.EntityId,
                    TargetVersion = job.TargetVersion
                }, terminal ? "failed" : "queued", cancellationToken);
                logger.LogWarning("Recovered expired lease for embedding job {JobId}; attempt {Attempt}.", job.Id, attempts);
            }
        }
    }

    private async Task RunHeartbeatAsync(Guid jobId, Guid lockToken, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                using var scope = scopeFactory.CreateScope();
                var heartbeatDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var updated = await heartbeatDb.EmbeddingJobs
                    .Where(item => item.Id == jobId && item.Status == "processing" && item.LockToken == lockToken)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.HeartbeatAt, now)
                        .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                if (updated == 0) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding job {JobId} heartbeat failed.", jobId);
        }
    }

    private static async Task AwaitHeartbeatShutdownAsync(Task? heartbeatTask)
    {
        if (heartbeatTask is null) return;
        try { await heartbeatTask; }
        catch (OperationCanceledException) { }
    }

    private static async Task<int?> GetCurrentVersionAsync(AppDbContext db, string entityType, int entityId, CancellationToken cancellationToken) =>
        entityType == "listing"
            ? await db.Listings.AsNoTracking().Where(item => item.Id == entityId).Select(item => (int?)item.ContentVersion).SingleOrDefaultAsync(cancellationToken)
            : entityType == "requirement"
                ? await db.Requirements.AsNoTracking().Where(item => item.Id == entityId).Select(item => (int?)item.ContentVersion).SingleOrDefaultAsync(cancellationToken)
                : null;

    private async Task SupersedeAsync(AppDbContext db, Models.EmbeddingJob job, Guid lockToken, int? currentVersion, CancellationToken cancellationToken)
    {
        await db.EmbeddingJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockToken == lockToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "superseded")
                .SetProperty(item => item.CompletedAt, DateTime.UtcNow)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.LockToken, (Guid?)null)
                .SetProperty(item => item.HeartbeatAt, (DateTime?)null)
                .SetProperty(item => item.LastError, currentVersion.HasValue ? $"Superseded by content version {currentVersion.Value}." : "The source record was deleted.")
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);

        if (currentVersion.HasValue)
        {
            await SetEntityStatusAsync(db, job, "queued", cancellationToken, currentVersion.Value);
            using var enqueueScope = scopeFactory.CreateScope();
            var queue = enqueueScope.ServiceProvider.GetRequiredService<IEmbeddingJobService>();
            await queue.EnqueueAsync(job.EntityType, job.EntityId, cancellationToken);
        }
    }

    private static Task<int> CompleteEntityAsync(AppDbContext db, Models.EmbeddingJob job, CancellationToken cancellationToken) =>
        job.EntityType == "listing"
            ? db.Listings.Where(item => item.Id == job.EntityId && item.ContentVersion == job.TargetVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.EmbeddingVersion, job.TargetVersion)
                    .SetProperty(item => item.EmbeddingStatus, "completed"), cancellationToken)
            : db.Requirements.Where(item => item.Id == job.EntityId && item.ContentVersion == job.TargetVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.EmbeddingVersion, job.TargetVersion)
                    .SetProperty(item => item.EmbeddingStatus, "completed"), cancellationToken);

    private static Task<int> SetEntityStatusAsync(AppDbContext db, Models.EmbeddingJob job, string status, CancellationToken cancellationToken, int? version = null)
    {
        var targetVersion = version ?? job.TargetVersion;
        return job.EntityType == "listing"
            ? db.Listings.Where(item => item.Id == job.EntityId && item.ContentVersion == targetVersion)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EmbeddingStatus, status), cancellationToken)
            : db.Requirements.Where(item => item.Id == job.EntityId && item.ContentVersion == targetVersion)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.EmbeddingStatus, status), cancellationToken);
    }

    private static Task<int> ResetEntityEmbeddingAsync(AppDbContext db, string entityType, int entityId, CancellationToken cancellationToken) =>
        entityType == "listing"
            ? ResetListingEmbeddingAsync(db, entityId, cancellationToken)
            : ResetRequirementEmbeddingAsync(db, entityId, cancellationToken);

    private static Task<int> ResetListingEmbeddingAsync(AppDbContext db, int listingId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE listings SET embedding = NULL, embedding_model = NULL WHERE listingid = {listingId}", cancellationToken);

    private static Task<int> ResetRequirementEmbeddingAsync(AppDbContext db, int requirementId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE requirements SET embedding = NULL, embedding_model = NULL WHERE requirementid = {requirementId}", cancellationToken);
}
