using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class InventoryNormalizationTests
{
    [Theory]
    [InlineData("Flat / Apartment", "APARTMENT")]
    [InlineData("INDEPENDENTHOUSE", "INDEPENDENT_HOUSE")]
    [InlineData("Commercial Office", "OFFICE")]
    [InlineData("Agricultural Land", "AGRICULTURAL_LAND")]
    [InlineData("Godown", "WAREHOUSE")]
    public void PropertyType_CollapsesKnownAliases(string input, string expected) =>
        Assert.Equal(expected, InventoryNormalization.PropertyType(input));

    [Theory]
    [InlineData("Bare", "UNFURNISHED")]
    [InlineData("Semi-Furnished", "SEMI_FURNISHED")]
    [InlineData("Fully Furnished", "FURNISHED")]
    public void Furnishing_CollapsesKnownAliases(string input, string expected) =>
        Assert.Equal(expected, InventoryNormalization.Furnishing(input));

    [Theory]
    [InlineData("NE", "NORTH_EAST")]
    [InlineData("South-West", "SOUTH_WEST")]
    public void Facing_CollapsesKnownAliases(string input, string expected) =>
        Assert.Equal(expected, InventoryNormalization.Facing(input));

    [Fact]
    public void Configurations_NormalizesAndDeduplicatesValues()
    {
        Assert.Equal(["2BHK", "3BHK"], InventoryNormalization.Configurations(["2 BHK", "2BHK", "3 BHK"]));
    }

    [Theory]
    [InlineData("fixed", "FIXED")]
    [InlineData("negotiable", "FLEXIBLE")]
    [InlineData("discuss", "NOBUDGET")]
    public void BudgetType_NormalizesSupportedModes(string input, string expected) =>
        Assert.Equal(expected, InventoryNormalization.BudgetType(input));
}
