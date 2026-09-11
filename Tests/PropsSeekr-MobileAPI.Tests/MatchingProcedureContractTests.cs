using Xunit;

namespace PropSeekr.Tests;

public sealed class MatchingProcedureContractTests
{
    private static readonly string ProcedureSql = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "scripts", "harden-matching-engine.sql"));

    [Fact]
    public void Procedure_PreservesPrecisionFirstHardFilters()
    {
        Assert.Contains("l.broker_id <> r.broker_id", ProcedureSql);
        Assert.Contains("LOWER(BTRIM(l.resolved_city)) = LOWER(BTRIM(r.resolved_city))", ProcedureSql);
        Assert.Contains("preferred.distance_km <= r.radius_km", ProcedureSql);
        Assert.Contains("l.normalized_price <= r.normalized_budget * 1.10", ProcedureSql);
        Assert.Contains("REGEXP_REPLACE(UPPER(BTRIM(required_configuration))", ProcedureSql);
        Assert.Contains("locality_geocoding_status IN ('resolved', 'verified')", ProcedureSql);
        Assert.Contains("pm.geocoding_status, ''), 'pending') IN ('resolved', 'verified')", ProcedureSql);
    }

    [Fact]
    public void Procedure_PreservesProgressedMatchesDuringRebuild()
    {
        Assert.Contains("LOCK TABLE public.matches IN SHARE ROW EXCLUSIVE MODE", ProcedureSql);
        Assert.Contains("SET status = 'STALE'", ProcedureSql);
        Assert.DoesNotContain("DELETE FROM public.matches", ProcedureSql);
        Assert.Contains("UPPER(COALESCE(m.status, '')) = 'MATCHED'", ProcedureSql);
        Assert.Contains("p_requirement_id IS NULL OR m.requirement_id = p_requirement_id", ProcedureSql);
        Assert.Contains("p_listing_id IS NULL OR m.listing_id = p_listing_id", ProcedureSql);
        Assert.Contains("public.match_connection_requests", ProcedureSql);
        Assert.Contains("public.match_confirmations", ProcedureSql);
        Assert.Contains("public.reveals", ProcedureSql);
        Assert.Contains("UPPER(COALESCE(matches.status, '')) IN ('MATCHED', 'STALE')", ProcedureSql);
    }

    [Fact]
    public void Procedure_ContainsNewStructuredPreferenceScores()
    {
        Assert.Contains("AS facing_score", ProcedureSql);
        Assert.Contains("AS project_score", ProcedureSql);
        Assert.Contains("requirement_size_max", ProcedureSql);
        Assert.Contains("normalized_budget_min", ProcedureSql);
        Assert.Contains("r.preferred_project_names", ProcedureSql);
    }

    [Fact]
    public void Procedure_RequiresTrustedHyperlocalScopeAndConsistentNormalization()
    {
        Assert.Contains("COALESCE(cardinality(r.preferred_locality_ids), 0) > 0", ProcedureSql);
        Assert.DoesNotContain("preferred.locality_similarity >= 0.60", ProcedureSql);
        Assert.Contains("array_position(r.preferred_locality_ids, locality.masterid)", ProcedureSql);
        Assert.Contains("REGEXP_REPLACE(UPPER(BTRIM(c.listing_configuration))", ProcedureSql);
        Assert.Contains("COALESCE(r.radius_km, 3.0)", ProcedureSql);
        Assert.DoesNotContain("NULLIF(r.radius_km, 0)", ProcedureSql);
    }

    [Fact]
    public void Procedure_RejectsExpiredInventoryAndUnsafeUnitFallbacks()
    {
        Assert.Contains("r.expires_at IS NULL OR r.expires_at > NOW()", ProcedureSql);
        Assert.Contains("l.expires_at IS NULL OR l.expires_at > NOW()", ProcedureSql);
        Assert.Contains("l.normalized_price_unit <> 'PER_MONTH'", ProcedureSql);
        Assert.Contains("r.normalized_budget_unit <> 'PER_MONTH'", ProcedureSql);
        Assert.DoesNotContain("r.size / 12000.0", ProcedureSql);
        Assert.DoesNotContain("l.size / 12000.0", ProcedureSql);
    }
}
