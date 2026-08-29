using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/bulk-imports")]
public sealed class BulkImportsController(AppDbContext db, IBrokerIdentityService brokerIdentityService, IConfiguration configuration) : ControllerBase
{
    [HttpPost("uploads")]
    public async Task<IActionResult> CreateUpload([FromBody] CreateBulkImportUploadRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.FileName) || !request.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = "Only .txt files are supported." });
        var brokerId = await brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
        if (!brokerId.HasValue) return NotFound(new { success = false, message = "No broker profile is linked to this account." });
        var bucket = configuration["FileProcessor:S3BucketName"] ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME");
        if (string.IsNullOrWhiteSpace(bucket)) return StatusCode(503, new { success = false, message = "Bulk import storage is not configured." });

        var safeFileName = Path.GetFileName(request.FileName);
        var job = new BulkImportJob { BrokerId = brokerId.Value, OriginalFileName = safeFileName, StorageKey = $"bulk-imports/{brokerId}/{Guid.NewGuid():N}.txt" };
        db.BulkImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        using var s3 = new AmazonS3Client();
        var uploadUrl = s3.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = bucket, Key = job.StorageKey, Verb = HttpVerb.PUT, ContentType = "text/plain", Expires = DateTime.UtcNow.AddMinutes(15) });
        return Ok(new { success = true, job_id = job.Id, upload_url = uploadUrl, expires_at = DateTime.UtcNow.AddMinutes(15) });
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
        return Ok(new { success = true, job_id = item.Id, status = item.Status, listings_inserted = item.ListingsInserted, requirements_inserted = item.RequirementsInserted, skipped_records = item.SkippedRecords, failed_records = item.FailedRecords, attempt_count = item.AttemptCount, max_attempts = item.MaxAttempts, last_error = item.Status == "failed" ? item.LastError : null, completed_at = item.CompletedAt });
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

public sealed class CreateBulkImportUploadRequest { public string FileName { get; init; } = string.Empty; }
