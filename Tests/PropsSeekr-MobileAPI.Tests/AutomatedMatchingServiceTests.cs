using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class AutomatedMatchingServiceTests
{
    [Theory]
    [InlineData("APARTMENT", "FLAT")]
    [InlineData("Flat/Apartment", "APARTMENT")]
    [InlineData("OFFICE_SPACE", "OFFICE")]
    public void EquivalentPropertyTypes_IncludesCommonDatabaseAliases(string input, string expected)
    {
        Assert.Contains(expected, AutomatedMatchingService.EquivalentPropertyTypes(input));
    }

    [Theory]
    [InlineData("1BHK", "1 BHK", true)]
    [InlineData("1BHK", "2BHK", false)]
    public void ConfigurationMatches_NormalizesSpacing(string listing, string requirement, bool expected)
    {
        Assert.Equal(expected, AutomatedMatchingService.ConfigurationMatches(listing, [requirement]));
    }

    [Fact]
    public void ConfigurationMatches_AllowsRequirementWithoutConfiguration()
    {
        Assert.True(AutomatedMatchingService.ConfigurationMatches("1BHK", []));
    }
}
