using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PropSeekr.Data;
using PropSeekr.Models;
using propseekr_file_processor;

namespace PropSeekr.Services;

/// <summary>
/// Resumable, precision-first repair of legacy location data. Each pass processes
/// one bounded batch and reruns matching only for inventory whose locality became trusted.
/// </summary>
public sealed class LocationRemediationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LocationRemediationWorker> logger) : BackgroundService
{
    private static readonly Regex LabelledLocation = new(
        @"(?im)^\s*(?:location|locality|loc|address|area)\s*[:\-]\s*(?<value>[^\r\n]{3,120})",
        RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = await ProcessOneBatchAsync(stoppingToken);
                await Task.Delay(worked ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Location remediation worker iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        await db.LocationRemediationJobs
            .Where(job => job.Status == "processing" && job.LockedAt < now.AddMinutes(-5))
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "queued")
                .SetProperty(job => job.LockToken, (Guid?)null)
                .SetProperty(job => job.LockedAt, (DateTime?)null)
                .SetProperty(job => job.AvailableAt, now)
                .SetProperty(job => job.LastError, "Worker lease expired; job was resumed.")
                .SetProperty(job => job.UpdatedAt, now), cancellationToken);

        var candidate = await db.LocationRemediationJobs.AsNoTracking()
            .Where(job => job.Status == "queued" && job.AvailableAt <= now)
            .OrderBy(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;

        var lockToken = Guid.NewGuid();
        var claimed = await db.LocationRemediationJobs
            .Where(job => job.Id == candidate.Id && job.Status == "queued")
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "processing")
                .SetProperty(job => job.LockToken, lockToken)
                .SetProperty(job => job.LockedAt, now)
                .SetProperty(job => job.UpdatedAt, now), cancellationToken);
        if (claimed == 0) return true;

        try
        {
            var job = await db.LocationRemediationJobs.SingleAsync(item => item.Id == candidate.Id, cancellationToken);
            await ProcessStageAsync(db, job, cancellationToken);
            job.LockToken = null;
            job.LockedAt = null;
            job.UpdatedAt = DateTime.UtcNow;
            if (job.Stage == "complete")
            {
                job.Status = "completed";
                job.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                job.Status = "queued";
                job.AvailableAt = DateTime.UtcNow;
            }
            job.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await db.LocationRemediationJobs
                .Where(job => job.Id == candidate.Id && job.LockToken == lockToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(job => job.Status, "queued")
                    .SetProperty(job => job.LockToken, (Guid?)null)
                    .SetProperty(job => job.LockedAt, (DateTime?)null)
                    .SetProperty(job => job.AvailableAt, DateTime.UtcNow)
                    .SetProperty(job => job.LastError, "API stopped; remediation will resume from its last saved cursor.")
                    .SetProperty(job => job.UpdatedAt, DateTime.UtcNow), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Location remediation job {JobId} failed.", candidate.Id);
            await db.LocationRemediationJobs
                .Where(job => job.Id == candidate.Id && job.LockToken == lockToken)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(job => job.Status, "queued")
                    .SetProperty(job => job.LockToken, (Guid?)null)
                    .SetProperty(job => job.LockedAt, (DateTime?)null)
                    .SetProperty(job => job.AvailableAt, DateTime.UtcNow.AddSeconds(30))
                    .SetProperty(job => job.LastError, Truncate(ex.Message, 2000))
                    .SetProperty(job => job.UpdatedAt, DateTime.UtcNow), cancellationToken);
        }

        return true;
    }

    private static async Task ProcessStageAsync(
        AppDbContext db,
        LocationRemediationJob job,
        CancellationToken cancellationToken)
    {
        if (job.Stage == "master")
        {
            var rows = await db.MasterLocations.AsNoTracking()
                .Where(location => location.Id > job.CursorId &&
                    (location.Latitude == null || location.Longitude == null) &&
                    (location.GeocodingStatus == "pending" || location.GeocodingStatus == "provider_error"))
                .OrderBy(location => location.Id)
                .Take(job.BatchSize)
                .Select(location => new { location.Id, location.Area, location.City })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                job.Stage = "listings";
                job.CursorId = 0;
                return;
            }

            using var geocoder = new GeocodingService();
            foreach (var row in rows)
            {
                var result = await geocoder.GeocodeDetailedAsync(
                    row.Area ?? string.Empty,
                    string.IsNullOrWhiteSpace(row.City) ? job.DefaultCity : row.City,
                    cancellationToken);
                await db.MasterLocations.Where(location => location.Id == row.Id)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(location => location.Latitude, result.Latitude.HasValue ? (double?)result.Latitude.Value : null)
                        .SetProperty(location => location.Longitude, result.Longitude.HasValue ? (double?)result.Longitude.Value : null)
                        .SetProperty(location => location.GeocodingStatus, result.Status)
                        .SetProperty(location => location.GeocodingProvider, result.Provider)
                        .SetProperty(location => location.ProviderPlaceId, result.PlaceId)
                        .SetProperty(location => location.FormattedAddress, result.FormattedAddress)
                        .SetProperty(location => location.LocationPrecision, result.Precision)
                        .SetProperty(location => location.GeocodingConfidence, result.Confidence)
                        .SetProperty(location => location.GeocodedAt, DateTime.UtcNow)
                        .SetProperty(location => location.GeocodingError, Truncate(result.Error, 1000))
                        .SetProperty(location => location.ReviewRequired, !result.IsResolved), cancellationToken);
                if (result.IsResolved) job.MasterResolved++;
                else job.ReviewRequired++;
                job.CursorId = row.Id;
            }
            return;
        }

        if (job.Stage == "listings")
        {
            var rows = await db.Listings.AsNoTracking()
                .Where(item => item.Id > job.CursorId && item.MasterId == null &&
                    item.LocationResolutionStatus != "review_required")
                .OrderBy(item => item.Id)
                .Take(job.BatchSize)
                .Select(item => new { item.Id, item.RawMessageText, item.ProjectName, item.City })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                job.Stage = "requirements";
                job.CursorId = 0;
                return;
            }

            var trustedLocations = await LoadTrustedLocationCandidatesAsync(db, cancellationToken);
            foreach (var row in rows)
            {
                var city = CityExtractor.NormalizeDefaultCity(row.City ?? job.DefaultCity);
                var masterId = await ResolveHistoricalCandidateAsync(
                    db, row.RawMessageText, row.ProjectName, city, trustedLocations, cancellationToken);
                var resolved = masterId.HasValue;
                await db.Listings.Where(item => item.Id == row.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.MasterId, masterId)
                    .SetProperty(item => item.City, city)
                    .SetProperty(item => item.LocationResolutionStatus, resolved ? "resolved" : "review_required")
                    .SetProperty(item => item.LocationResolutionNote, resolved
                        ? "Recovered from historical source text and a trusted canonical locality."
                        : "No unambiguous trusted locality was found in the historical source text.")
                    .SetProperty(item => item.LocationResolvedAt, resolved ? DateTime.UtcNow : null), cancellationToken);
                if (resolved)
                {
                    job.ListingsResolved++;
                    await RunTargetedMatchingAsync(db, null, row.Id, cancellationToken);
                }
                else job.ReviewRequired++;
                job.CursorId = row.Id;
            }
            return;
        }

        if (job.Stage == "requirements")
        {
            var rows = await db.Requirements.AsNoTracking()
                .Where(item => item.Id > job.CursorId &&
                    (item.PreferredLocalityIds == null || item.PreferredLocalityIds.Length == 0) &&
                    item.LocationResolutionStatus != "review_required")
                .OrderBy(item => item.Id)
                .Take(job.BatchSize)
                .Select(item => new { item.Id, item.RawMessageText, item.City })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                job.Stage = "linked_listings";
                job.CursorId = 0;
                return;
            }

            var trustedLocations = await LoadTrustedLocationCandidatesAsync(db, cancellationToken);
            foreach (var row in rows)
            {
                var city = CityExtractor.NormalizeDefaultCity(row.City ?? job.DefaultCity);
                var masterId = await ResolveHistoricalCandidateAsync(
                    db, row.RawMessageText, null, city, trustedLocations, cancellationToken);
                var resolved = masterId.HasValue;
                await db.Requirements.Where(item => item.Id == row.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.PreferredLocalityIds, resolved ? new[] { masterId!.Value } : Array.Empty<int>())
                    .SetProperty(item => item.City, city)
                    .SetProperty(item => item.LocationResolutionStatus, resolved ? "resolved" : "review_required")
                    .SetProperty(item => item.LocationResolutionNote, resolved
                        ? "Recovered from historical source text and a trusted canonical locality."
                        : "No unambiguous trusted locality was found in the historical source text.")
                    .SetProperty(item => item.LocationResolvedAt, resolved ? DateTime.UtcNow : null), cancellationToken);
                if (resolved)
                {
                    job.RequirementsResolved++;
                    await RunTargetedMatchingAsync(db, row.Id, null, cancellationToken);
                }
                else job.ReviewRequired++;
                job.CursorId = row.Id;
            }
            return;
        }

        if (job.Stage == "linked_listings")
        {
            var rows = await db.Listings.AsNoTracking()
                .Where(item => item.Id > job.CursorId && item.MasterId != null &&
                    item.LocationResolutionStatus == "review_required" &&
                    item.MasterLocation != null &&
                    (item.MasterLocation.GeocodingStatus == "resolved" ||
                     item.MasterLocation.GeocodingStatus == "verified"))
                .OrderBy(item => item.Id)
                .Take(job.BatchSize)
                .Select(item => new { item.Id, item.MasterId, MasterCity = item.MasterLocation!.City })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                job.Stage = "linked_requirements";
                job.CursorId = 0;
                return;
            }

            foreach (var row in rows)
            {
                await db.Listings.Where(item => item.Id == row.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.City, item => string.IsNullOrEmpty(item.City)
                        ? (row.MasterCity ?? job.DefaultCity)
                        : item.City)
                    .SetProperty(item => item.LocationResolutionStatus, "resolved")
                    .SetProperty(item => item.LocationResolutionNote,
                        "Canonical locality coordinates were repaired by the remediation job.")
                    .SetProperty(item => item.LocationResolvedAt, DateTime.UtcNow), cancellationToken);
                job.ListingsResolved++;
                job.CursorId = row.Id;
                await RunTargetedMatchingAsync(db, null, row.Id, cancellationToken);
            }
            return;
        }

        if (job.Stage == "linked_requirements")
        {
            var rows = await db.Requirements.AsNoTracking()
                .Where(item => item.Id > job.CursorId &&
                    item.LocationResolutionStatus == "review_required" &&
                    item.PreferredLocalityIds != null && item.PreferredLocalityIds.Length > 0)
                .OrderBy(item => item.Id)
                .Take(job.BatchSize)
                .Select(item => new { item.Id, item.PreferredLocalityIds })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                job.Stage = "complete";
                job.CursorId = 0;
                return;
            }

            foreach (var row in rows)
            {
                var localityIds = row.PreferredLocalityIds ?? [];
                var trusted = await db.MasterLocations.AsNoTracking().AnyAsync(location =>
                    localityIds.Contains(location.Id) &&
                    (location.GeocodingStatus == "resolved" || location.GeocodingStatus == "verified"),
                    cancellationToken);
                if (trusted)
                {
                    await db.Requirements.Where(item => item.Id == row.Id).ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.LocationResolutionStatus, "resolved")
                        .SetProperty(item => item.LocationResolutionNote,
                            "Canonical locality coordinates were repaired by the remediation job.")
                        .SetProperty(item => item.LocationResolvedAt, DateTime.UtcNow), cancellationToken);
                    job.RequirementsResolved++;
                    await RunTargetedMatchingAsync(db, row.Id, null, cancellationToken);
                }
                job.CursorId = row.Id;
            }
        }
    }

    private static async Task<int?> ResolveHistoricalCandidateAsync(
        AppDbContext db,
        string? rawText,
        string? projectName,
        string city,
        IReadOnlyDictionary<string, List<TrustedLocationCandidate>> trustedLocations,
        CancellationToken cancellationToken)
    {
        var searchable = Normalize($"{projectName} {rawText}");
        if (string.IsNullOrWhiteSpace(searchable)) return null;

        var candidates = trustedLocations.GetValueOrDefault(Normalize(city)) ?? [];

        var matches = candidates
            .Where(candidate => ContainsPhrase(searchable, Normalize(candidate.Area)))
            .OrderByDescending(candidate => Normalize(candidate.Area).Length)
            .ToList();
        if (matches.Count > 0)
        {
            var bestLength = Normalize(matches[0].Area).Length;
            var equallySpecific = matches.Count(candidate => Normalize(candidate.Area).Length == bestLength);
            return equallySpecific == 1 ? matches[0].Id : null;
        }

        var labelled = LabelledLocation.Match(rawText ?? string.Empty);
        if (!labelled.Success) return null;
        var area = labelled.Groups["value"].Value.Trim(' ', ',', '.', '-', ':');
        if (area.Length is < 3 or > 100) return null;

        await db.Database.OpenConnectionAsync(cancellationToken);
        using var geocoder = new GeocodingService();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var resolution = await IngestService.ResolveOrCreateMasterAsync(connection, area, city, geocoder);
        return resolution.IsTrusted ? resolution.MasterId : null;
    }

    private static async Task<IReadOnlyDictionary<string, List<TrustedLocationCandidate>>>
        LoadTrustedLocationCandidatesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.MasterLocations.AsNoTracking()
            .Where(location => location.City != null && location.Area != null &&
                location.Latitude != null && location.Longitude != null &&
                (location.GeocodingStatus == "resolved" || location.GeocodingStatus == "verified"))
            .Select(location => new { location.Id, location.Area, location.City })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(location => Normalize(location.City))
            .ToDictionary(
                group => group.Key,
                group => group.Select(location =>
                    new TrustedLocationCandidate(location.Id, location.Area!)).ToList());
    }

    private static async Task RunTargetedMatchingAsync(
        AppDbContext db,
        int? requirementId,
        int? listingId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"CALL public.sp_run_matching_engine({requirementId}, {listingId})",
            cancellationToken);
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        phrase.Length >= 3 && Regex.IsMatch(text, $@"(?:^| ){Regex.Escape(phrase)}(?: |$)");

    private static string Normalize(string? value) => Regex.Replace(
        (value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];

    private sealed record TrustedLocationCandidate(int Id, string Area);
}
