using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.FileProcessing;

namespace PropSeekr.Services;

public sealed class BulkImportJobWorker(IServiceScopeFactory scopeFactory, ILogger<BulkImportJobWorker> logger, IConfiguration configuration) : BackgroundService
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
                .SetProperty(x => x.AvailableAt, now).SetProperty(x => x.LastError, "Worker lease expired; import was requeued.")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        var job = await db.BulkImportJobs.AsNoTracking().Where(item => item.Status == "queued" && item.AvailableAt <= now).OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return false;
        var claimed = await db.BulkImportJobs.Where(item => item.Id == job.Id && item.Status == "queued").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "processing").SetProperty(x => x.LockedAt, now).SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return true;
        try
        {
            var bucket = configuration["FileProcessor:S3BucketName"] ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
                ?? throw new InvalidOperationException("Bulk import storage is not configured.");
            var host = scope.ServiceProvider.GetRequiredService<FileProcessorHost>();
            var result = await host.Processor.RunBulkImportAsync(bucket, job.StorageKey, new RestLambdaContext(logger, job.Id.ToString("N")), cancellationToken);
            await db.BulkImportJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockedAt == now).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "completed").SetProperty(x => x.CompletedAt, DateTime.UtcNow).SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.ListingsInserted, result.ListingsInserted).SetProperty(x => x.RequirementsInserted, result.RequirementsInserted)
                .SetProperty(x => x.SkippedRecords, result.Skipped).SetProperty(x => x.FailedRecords, result.Failed).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
        }
        catch (Exception ex)
        {
            var attempts = job.AttemptCount + 1;
            var terminal = attempts >= job.MaxAttempts;
            var errorMessage = ex.Message.Length > 2000 ? ex.Message.Substring(0, 2000) : ex.Message;
            await db.BulkImportJobs.Where(item => item.Id == job.Id && item.Status == "processing" && item.LockedAt == now).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AttemptCount, attempts).SetProperty(x => x.Status, terminal ? "failed" : "queued")
                .SetProperty(x => x.AvailableAt, DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, attempts)))).SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LastError, errorMessage).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
            logger.LogWarning(ex, "Bulk import job {JobId} failed on attempt {Attempt}.", job.Id, attempts);
        }
        return true;
    }
}
