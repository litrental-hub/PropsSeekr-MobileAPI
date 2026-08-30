using System.Security.Claims;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using propseekr_file_processor;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/bulk-imports")]
public sealed class BulkImportsController(
    AppDbContext db,
    IBrokerIdentityService brokerIdentityService,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<BulkImportsController> logger) : ControllerBase
{
    private const long DefaultMaximumUploadBytes = 10 * 1024 * 1024;

    [HttpPost("uploads")]
    public async Task<IActionResult> CreateUpload([FromBody] CreateBulkImportUploadRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.FileName) || !request.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = "Only .txt files are supported." });
        var brokerId = await brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
        if (!brokerId.HasValue) return NotFound(new { success = false, message = "No broker profile is linked to this account." });

        var safeFileName = Path.GetFileName(request.FileName);
        var job = new BulkImportJob
        {
            BrokerId = brokerId.Value,
            OriginalFileName = safeFileName,
            DefaultCity = CityExtractor.NormalizeDefaultCity(request.DefaultCity)
        };

        if (environment.IsDevelopment())
        {
            job.StorageKey = $"{LocalBulkImportStorage.StorageKeyPrefix}{job.Id:N}.txt";
            db.BulkImportJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                job_id = job.Id,
                default_city = job.DefaultCity,
                upload_mode = "local",
                upload_endpoint = $"/bulk-imports/{job.Id}/content"
            });
        }

        var bucket = configuration["FileProcessor:S3BucketName"] ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
        if (string.IsNullOrWhiteSpace(bucket))
            return StatusCode(503, new { success = false, message = "Bulk import storage is not configured." });

        job.StorageKey = $"bulk-imports/{brokerId}/{job.Id:N}.txt";
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        string uploadUrl;
        try
        {
            using var s3 = new AmazonS3Client();
            uploadUrl = s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = job.StorageKey,
                Verb = HttpVerb.PUT,
                ContentType = "text/plain",
                Expires = expiresAt
            });
        }
        catch (AmazonClientException ex)
        {
            logger.LogError(ex, "Unable to create the S3 upload URL for bulk import {JobId}.", job.Id);
            return StatusCode(503, new
            {
                success = false,
                message = "Bulk import storage credentials are unavailable. Please try again later."
            });
        }

        db.BulkImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, job_id = job.Id, default_city = job.DefaultCity, upload_mode = "s3", upload_url = uploadUrl, expires_at = expiresAt });
    }

    [HttpPost("{id:guid}/content")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(DefaultMaximumUploadBytes)]
    public async Task<IActionResult> UploadLocalContent(
        Guid id,
        [FromForm] LocalBulkImportFileRequest request,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return NotFound();

        var ownedJob = await GetOwnedJobAsync(id, cancellationToken);
        if (ownedJob.Result is not null) return ownedJob.Result;
        var job = ownedJob.Job!;

        if (!LocalBulkImportStorage.IsLocalKey(job.StorageKey)) return NotFound();
        if (job.Status != "awaiting_upload")
            return Conflict(new { success = false, message = "This import has already been submitted." });

        var file = request.File;
        if (file is null || file.Length == 0)
            return BadRequest(new { success = false, message = "Choose a non-empty .txt file." });
        if (!Path.GetFileName(file.FileName).EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = "Only .txt files are supported." });

        var maximumUploadBytes = configuration.GetValue<long?>("BulkImports:MaximumUploadBytes")
            ?? DefaultMaximumUploadBytes;
        if (file.Length > maximumUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                success = false,
                message = $"The file exceeds the {maximumUploadBytes / (1024 * 1024)} MB upload limit."
            });

        var directory = LocalBulkImportStorage.GetDirectory(configuration, environment);
        Directory.CreateDirectory(directory);
        var destinationPath = LocalBulkImportStorage.GetInputPath(id, configuration, environment);
        var temporaryPath = destinationPath + ".uploading";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            System.IO.File.Move(temporaryPath, destinationPath, overwrite: true);
            var updated = await db.BulkImportJobs
                .Where(item => item.Id == id && item.Status == "awaiting_upload")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "queued")
                    .SetProperty(x => x.AvailableAt, DateTime.UtcNow)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);

            if (updated == 0)
            {
                System.IO.File.Delete(destinationPath);
                return Conflict(new { success = false, message = "This import has already been submitted." });
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
        }

        return Accepted(new { success = true, job_id = id, status = "queued" });
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken)
    {
        var job = await GetOwnedJobAsync(id, cancellationToken);
        if (job.Result is not null) return job.Result;
        if (job.Job!.Status != "awaiting_upload") return Conflict(new { success = false, message = "This import has already been submitted." });
        await db.BulkImportJobs.Where(item => item.Id == id && item.Status == "awaiting_upload").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "queued").SetProperty(x => x.AvailableAt, DateTime.UtcNow).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
        return Accepted(new { success = true, job_id = id, status = "queued" });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await GetOwnedJobAsync(id, cancellationToken);
        if (job.Result is not null) return job.Result;
        var item = job.Job!;
        return Ok(new { success = true, job_id = item.Id, default_city = item.DefaultCity, status = item.Status, listings_inserted = item.ListingsInserted, requirements_inserted = item.RequirementsInserted, skipped_records = item.SkippedRecords, failed_records = item.FailedRecords, attempt_count = item.AttemptCount, max_attempts = item.MaxAttempts, last_error = item.Status == "failed" ? item.LastError : null, completed_at = item.CompletedAt });
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        var job = await GetOwnedJobAsync(id, cancellationToken);
        if (job.Result is not null) return job.Result;
        if (job.Job!.Status != "failed") return Conflict(new { success = false, message = "Only failed imports can be retried." });
        await db.BulkImportJobs.Where(item => item.Id == id && item.Status == "failed").ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "queued").SetProperty(x => x.AttemptCount, 0).SetProperty(x => x.LastError, (string?)null)
            .SetProperty(x => x.AvailableAt, DateTime.UtcNow).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
        return Accepted(new { success = true, job_id = id, status = "queued" });
    }

    private async Task<(BulkImportJob? Job, IActionResult? Result)> GetOwnedJobAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return (null, Unauthorized());
        var job = await db.BulkImportJobs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (job is null) return (null, NotFound(new { success = false, message = "Bulk import job not found." }));
        if (!User.IsInRole("Admin"))
        {
            var brokerId = await brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
            if (!brokerId.HasValue || brokerId != job.BrokerId) return (null, Forbid());
        }
        return (job, null);
    }
}

public sealed class CreateBulkImportUploadRequest
{
    public string FileName { get; init; } = string.Empty;
    public string? DefaultCity { get; init; }
}
public sealed class LocalBulkImportFileRequest { public IFormFile? File { get; init; } }
