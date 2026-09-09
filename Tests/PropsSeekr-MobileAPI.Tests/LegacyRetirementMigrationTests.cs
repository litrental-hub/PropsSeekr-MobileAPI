using Microsoft.EntityFrameworkCore.Migrations.Operations;
using PropSeekr.Migrations;
using Xunit;

namespace PropSeekr.Tests;

public sealed class LegacyRetirementMigrationTests
{
    [Fact]
    public void Retirement_GuardsEveryTableBeforeAnyDrop()
    {
        var operations = new RetireLegacyCompatibilityTables().UpOperations;
        var guard = Assert.IsType<SqlOperation>(operations[0]);
        Assert.False(guard.SuppressTransaction);
        Assert.Contains("ACCESS EXCLUSIVE MODE", guard.Sql);
        Assert.Contains("RAISE EXCEPTION", guard.Sql);
        var drops = operations.OfType<DropTableOperation>().ToArray();
        Assert.Equal(8, drops.Length);
        foreach (var drop in drops) Assert.Contains($"'{drop.Name}'", guard.Sql);
        Assert.DoesNotContain(drops, drop => drop.Name == "notifications");
    }
}
