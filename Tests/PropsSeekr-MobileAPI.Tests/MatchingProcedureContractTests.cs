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
    }

    [Fact]
    public void Procedure_PreservesProgressedMatchesDuringRebuild()
    {
        Assert.Contains("UPPER(COALESCE(m.status, '')) = 'MATCHED'", ProcedureSql);
        Assert.Contains("p_requirement_id IS NULL OR m.requirement_id = p_requirement_id", ProcedureSql);
        Assert.Contains("p_listing_id IS NULL OR m.listing_id = p_listing_id", ProcedureSql);
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
}
