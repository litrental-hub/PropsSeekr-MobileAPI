using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using propseekr_file_processor;

namespace PropSeekr.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/v1/location-remediation")]
public sealed class LocationRemediationController(AppDbContext db) : ControllerBase
{
    [HttpPost("jobs")]
    public async Task<IActionResult> Create(
        [FromBody] CreateLocationRemediationRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var active = await db.LocationRemediationJobs.AnyAsync(
            job => job.Status == "queued" || job.Status == "processing", cancellationToken);
        if (active)
            return Conflict(new { success = false, message = "A location remediation job is already active." });

        var job = new LocationRemediationJob
        {
            RequestedByUserId = userId,
            DefaultCity = CityExtractor.NormalizeDefaultCity(request.DefaultCity),
            BatchSize = Math.Clamp(request.BatchSize ?? 25, 1, 100)
        };
        db.LocationRemediationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return Accepted(new { success = true, job_id = job.Id, default_city = job.DefaultCity, status = job.Status });
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.LocationRemediationJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (job is null) return NotFound(new { success = false, message = "Location remediation job not found." });
        return Ok(new
        {
            success = true,
            job_id = job.Id,
            job.DefaultCity,
            job.Status,
            job.Stage,
            job.CursorId,
            job.BatchSize,
            job.MasterResolved,
            job.ListingsResolved,
            job.RequirementsResolved,
            job.ReviewRequired,
            job.LastError,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt
        });
    }
}

public sealed class CreateLocationRemediationRequest
{
    public string? DefaultCity { get; init; }
    public int? BatchSize { get; init; }
}
