using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.FileProcessing;

namespace PropSeekr.Services;

public sealed class BulkImportJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BulkImportJobWorker> logger,
    IConfiguration configuration,
    IHostEnvironment environment) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Bulk import worker loop failed."); await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var leaseCutoff = now.AddMinutes(-30);
        await db.BulkImportJobs.Where(item => item.Status == "processing" && item.LockedAt != null && item.LockedAt < leaseCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "queued").SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockToken, (Guid?)null)
                .SetProperty(x => x.AvailableAt, now).SetProperty(x => x.LastError, "Worker lease expired; import was requeued.")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        var job = await db.BulkImportJobs.AsNoTracking().Where(item => item.Status == "queued" && item.AvailableAt <= now).OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return false;
        var lockToken = Guid.NewGuid();
        var claimed = await db.BulkImportJobs.Where(item => item.Id == job.Id && item.Status == "queued").ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "processing")
            .SetProperty(x => x.LockedAt, now)
            .SetProperty(x => x.LockToken, lockToken)
            .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return true;
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = MaintainLeaseAsync(job.Id, lockToken, heartbeatCancellation.Token);
        try
        {
            var isLocal = LocalBulkImportStorage.IsLocalKey(job.StorageKey);
            if (isLocal && !environment.IsDevelopment())
                throw new InvalidOperationException("Local bulk import jobs can only run in Development.");

            var bucket = isLocal
                ? "local-development"
                : configuration["FileProcessor:S3BucketName"] ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
                    ?? throw new InvalidOperationException("Bulk import storage is not configured.");
            var host = scope.ServiceProvider.GetRequiredService<FileProcessorHost>();
            var result = await host.Processor.RunBulkImportAsync(
                bucket,
                job.StorageKey,
                new RestLambdaContext(logger, job.Id.ToString("N")),
                job.DefaultCity,
                cancellationToken);
            if (result.Failed > 0 && result.ListingsInserted == 0 && result.RequirementsInserted == 0)
                throw new InvalidOperationException(
                    $"All {result.Failed} extracted records failed during ingestion. {result.FirstFailure}");

            await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask);
            var completed = await db.BulkImportJobs.Where(item =>
                item.Id == job.Id && item.Status == "processing" && item.LockToken == lockToken).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "completed").SetProperty(x => x.CompletedAt, DateTime.UtcNow).SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockToken, (Guid?)null)
                .SetProperty(x => x.ListingsInserted, result.ListingsInserted).SetProperty(x => x.RequirementsInserted, result.RequirementsInserted)
                .SetProperty(x => x.SkippedRecords, result.Skipped).SetProperty(x => x.FailedRecords, result.Failed)
                .SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (completed == 0)
                throw new InvalidOperationException("Bulk import lease ownership was lost before completion.");

            if (isLocal)
            {
                DeleteIfPresent(LocalBulkImportStorage.GetInputPath(job.Id, configuration, environment));
                DeleteIfPresent(LocalBulkImportStorage.GetOutputPath(job.Id, configuration, environment));
            }
        }
        catch (Exception ex)
        {
            await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask);
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var errorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            await db.BulkImportJobs.Where(item =>
                item.Id == job.Id && item.Status == "processing" && item.LockToken == lockToken).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AttemptCount, attempts).SetProperty(x => x.Status, terminal ? "failed" : "queued")
                .SetProperty(x => x.AvailableAt, DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, attempts)))).SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockToken, (Guid?)null)
                .SetProperty(x => x.LastError, errorMessage).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
            logger.LogWarning(ex, "Bulk import job {JobId} failed on attempt {Attempt}.", job.Id, attempts);
        }
        return true;
    }

    private async Task MaintainLeaseAsync(Guid jobId, Guid lockToken, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    using var heartbeatScope = scopeFactory.CreateScope();
                    var heartbeatDb = heartbeatScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var heartbeatAt = DateTime.UtcNow;
                    var refreshed = await heartbeatDb.BulkImportJobs.Where(item =>
                        item.Id == jobId && item.Status == "processing" && item.LockToken == lockToken)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.LockedAt, heartbeatAt)
                            .SetProperty(x => x.UpdatedAt, heartbeatAt), cancellationToken);
                    if (refreshed == 0)
                    {
                        logger.LogWarning("Bulk import job {JobId} no longer owns lease {LockToken}.", jobId, lockToken);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A transient database interruption must not fault the heartbeat
                    // task and bypass the worker's normal completion/failure update.
                    // The next tick retries; ownership is still verified by lock token.
                    logger.LogWarning(ex, "Could not refresh bulk import lease {LockToken} for job {JobId}.", lockToken, jobId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task StopHeartbeatAsync(
        CancellationTokenSource cancellation,
        Task heartbeatTask)
    {
        if (!cancellation.IsCancellationRequested)
            await cancellation.CancelAsync();
        await heartbeatTask;
    }

    private void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove completed local bulk import file {Path}.", path);
        }
    }
}
