using propseekr_file_processor;
using Xunit;

namespace PropSeekr.Tests;

public sealed class CityExtractorTests
{
    [Theory]
    [InlineData(null, "Indore")]
    [InlineData("", "Indore")]
    [InlineData("  mumbai  ", "Mumbai")]
    [InlineData("@@invalid@@", "Indore")]
    public void NormalizeDefaultCity_UsesSafeIndoreFallback(string? input, string expected)
    {
        Assert.Equal(expected, CityExtractor.NormalizeDefaultCity(input));
    }

    [Fact]
    public void ExtractCity_ExplicitCityWinsOverUploadFallback()
    {
        Assert.Equal("Mumbai", CityExtractor.ExtractCity("Ghatkopar, Mumbai", "Indore"));
    }

    [Fact]
    public void ExtractCity_UsesUploadFallbackWhenSourceHasNoCity()
    {
        Assert.Equal("Pune", CityExtractor.ExtractCity("Baner Road", "Pune"));
    }
}
