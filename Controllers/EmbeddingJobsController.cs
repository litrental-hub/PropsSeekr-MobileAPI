using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/embedding-jobs")]
public sealed class EmbeddingJobsController(AppDbContext dbContext, IBrokerIdentityService brokerIdentityService) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var job = await dbContext.EmbeddingJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (job is null) return NotFound(new { success = false, message = "Embedding job not found." });
        var brokerId = await brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
        var ownerId = job.EntityType == "listing"
            ? await dbContext.Listings.Where(item => item.Id == job.EntityId).Select(item => (int?)item.BrokerId).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Requirements.Where(item => item.Id == job.EntityId).Select(item => (int?)item.BrokerId).SingleOrDefaultAsync(cancellationToken);
        if (!User.IsInRole("Admin") && (!brokerId.HasValue || brokerId != ownerId)) return Forbid();
        return Ok(new { success = true, job_id = job.Id, entity_type = job.EntityType, entity_id = job.EntityId, status = job.Status, attempt_count = job.AttemptCount, max_attempts = job.MaxAttempts, completed_at = job.CompletedAt, last_error = job.Status == "failed" ? job.LastError : null });
    }
}
