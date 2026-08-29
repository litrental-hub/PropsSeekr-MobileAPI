using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;

namespace PropSeekr.Services;

/// <summary>Retires unrevealed match decisions that were calculated from old inventory content.</summary>
public sealed class MatchInvalidationService(AppDbContext db)
{
    public async Task InvalidateForListingAsync(int listingId, CancellationToken cancellationToken = default)
    {
        var matchIds = await db.Matches
            .Where(match => match.ListingId == listingId && !db.Reveals.Any(reveal => reveal.MatchId == match.Id))
            .Select(match => match.Id)
            .ToListAsync(cancellationToken);
        await InvalidateAsync(matchIds, cancellationToken);
    }

    public async Task InvalidateForRequirementAsync(int requirementId, CancellationToken cancellationToken = default)
    {
        var matchIds = await db.Matches
            .Where(match => match.RequirementId == requirementId && !db.Reveals.Any(reveal => reveal.MatchId == match.Id))
            .Select(match => match.Id)
            .ToListAsync(cancellationToken);
        await InvalidateAsync(matchIds, cancellationToken);
    }

    private async Task InvalidateAsync(IReadOnlyCollection<int> matchIds, CancellationToken cancellationToken)
    {
        if (matchIds.Count == 0) return;
        var now = DateTime.UtcNow;
        await db.Matches.Where(match => matchIds.Contains(match.Id)).ExecuteUpdateAsync(setters => setters
            .SetProperty(match => match.Status, "INVALIDATED")
            .SetProperty(match => match.State, "expired")
            .SetProperty(match => match.StatusUpdatedAt, now), cancellationToken);
        await db.MatchConnectionRequests.Where(request => matchIds.Contains(request.MatchId) && request.Status == "pending")
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, "expired"), cancellationToken);
        await db.MatchConfirmations.Where(confirmation => matchIds.Contains(confirmation.MatchId)).ExecuteDeleteAsync(cancellationToken);
    }
}
