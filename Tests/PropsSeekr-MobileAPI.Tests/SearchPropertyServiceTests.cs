using PropSeekr.DTOs.Search;
using PropSeekr.Services;
using PropSeekr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Amazon.Lambda.APIGatewayEvents;
using PropSeekr.FileProcessing;
using propseekr_file_processor;
using System.Text.Json;
using Xunit;

namespace PropSeekr.Tests;

public sealed class SearchPropertyRequestTests
{
    [Fact]
    public void Validate_AcceptsFiveKilometreNearbySearch()
    {
        var request = ValidRequest();
        request.Validate();
        Assert.Equal(5, request.Location.RadiusKm);
        Assert.Equal(20, request.Pagination.Limit);
    }

    [Theory]
    [InlineData("", "SUPPLY")]
    [InlineData("RENTAL", "")]
    [InlineData("LEASE", "SUPPLY")]
    public void Validate_RejectsUnsupportedMarketplaceModes(string transaction, string listingType)
    {
        var request = ValidRequest();
        request.TransactionType = transaction;
        request.ListingType = listingType;
        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void Validate_RejectsMissingRadius()
    {
        var request = ValidRequest();
        request.Location.RadiusKm = 0;
        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void TransactionDirections_MapSupplyAndDemandCorrectly()
    {
        Assert.Equal(["RENT", "RENTAL"], SearchPropertyService.GetTransactionTypes("RENTAL", true));
        Assert.Contains("SELL", SearchPropertyService.GetTransactionTypes("BUY_SELL", true));
        Assert.Contains("BUY", SearchPropertyService.GetTransactionTypes("BUY_SELL", false));
        Assert.DoesNotContain("SELL", SearchPropertyService.GetTransactionTypes("BUY_SELL", false));
    }

    [Fact]
    public void Titles_UseOnlyStructuredOrStoredContent()
    {
        Assert.Equal("2BHK APARTMENT", SearchPropertyService.BuildListingTitle("2BHK", "APARTMENT", null));
        Assert.Equal("3BHK FLAT", SearchPropertyService.BuildRequirementTitle(["3BHK"], "FLAT"));
        Assert.Equal("Property listing", SearchPropertyService.BuildListingTitle(null, null, null));
        Assert.Equal("Property requirement", SearchPropertyService.BuildRequirementTitle([], null));
    }

    [Fact]
    public void DiscoveryContract_DoesNotExposeBrokerIdentityOrContactFields()
    {
        var response = new SearchPropertyResponseDto
        {
            Results = [new PropertySearchResultItemDto { Id = "1" }],
            Requirements = [new RequirementSearchResultItemDto { Id = "2" }]
        };

        var json = JsonSerializer.Serialize(response);
        Assert.False(json.Contains("broker", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("contact", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("phone", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("initials", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("description", StringComparison.OrdinalIgnoreCase));
    }

    private static SearchPropertyRequestDto ValidRequest() => new()
    {
        TransactionType = "RENTAL",
        ListingType = "SUPPLY",
        Location = new LocationDto { Lat = 22.7533, Lng = 75.8937, RadiusKm = 5 },
        Pagination = new PaginationDto { Page = 1, Limit = 20 }
    };
}

public sealed class LiveSearchFactAttribute : FactAttribute
{
    public LiveSearchFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PROPSEEKR_RUN_LIVE_SEARCH_SMOKE"), "1", StringComparison.Ordinal))
            Skip = "Set PROPSEEKR_RUN_LIVE_SEARCH_SMOKE=1 to run the read-only configured-database smoke test.";
    }
}

public sealed class SearchPropertyLiveSmokeTests
{
    [LiveSearchFact]
    public async Task ConfiguredDatabase_AcceptsCanonicalFiveKilometreQuery()
    {
        var appSettingsPath = FindAppSettings();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("DefaultConnection")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        await using var db = new AppDbContext(options);
        var service = new SearchPropertyService(db);
        var request = new SearchPropertyRequestDto
        {
            TransactionType = "BUY_SELL",
            ListingType = "SUPPLY",
            Location = new LocationDto { Lat = 19.0760, Lng = 72.8777, RadiusKm = 5 },
            Pagination = new PaginationDto { Page = 1, Limit = 5 }
        };
        var response = await service.SearchPropertiesAsync(request, Guid.NewGuid());

        Assert.Equal("success", response.Status);
        Assert.All(response.Results, result => Assert.InRange(result.DistanceKm!.Value, 0, 5));

        request.ListingType = "DEMAND";
        var demand = await service.SearchPropertiesAsync(request, Guid.NewGuid());
        Assert.Equal("success", demand.Status);
        Assert.All(demand.Requirements, result => Assert.InRange(result.DistanceKm!.Value, 0, 5));
    }

    [LiveSearchFact]
    public async Task ConfiguredDatabase_AcceptsCanonicalFileProcessorMatchesQuery()
    {
        var appSettingsPath = FindAppSettings();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("DefaultConnection")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var service = new MatchesApiService(connectionString!);
        var response = await service.HandleGetMatchesAsync(
            new APIGatewayProxyRequest
            {
                Path = "/matches",
                HttpMethod = "GET",
                QueryStringParameters = new Dictionary<string, string>
                {
                    ["page"] = "1",
                    ["size"] = "5"
                }
            },
            new RestLambdaContext(
                NullLogger.Instance,
                new DefaultHttpContext()));

        Assert.True(response.StatusCode == 200, response.Body);
        using var payload = JsonDocument.Parse(response.Body);
        Assert.True(payload.RootElement.TryGetProperty("matches", out _));
        Assert.True(payload.RootElement.TryGetProperty("pagination", out _));
    }

    private static string FindAppSettings()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate API appsettings.json for the live smoke test.");
    }
}

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SearchPropertyServiceIntegrationTests : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL");
    private AppDbContext? _db;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        _db = new AppDbContext(options);
        await CreateSchemaAsync(_db);
        await SeedAsync(_db);
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await _db.DisposeAsync();
    }

    [PostgreSqlFact]
    public async Task Search_UsesCanonicalRowsAndFiveKilometreRadiusForBothTabs()
    {
        Assert.NotNull(_db);
        var service = new SearchPropertyService(_db!);
        var request = new SearchPropertyRequestDto
        {
            TransactionType = "RENTAL",
            ListingType = "SUPPLY",
            Location = new LocationDto { Lat = 22.7533, Lng = 75.8937, RadiusKm = 5 },
            Filters = new FiltersDto { Configurations = ["2BHK"] },
            Pagination = new PaginationDto { Page = 1, Limit = 20 }
        };

        var supply = await service.SearchPropertiesAsync(request, Guid.NewGuid());
        Assert.Equal(1, supply.AvailableCount);
        Assert.Equal(1, supply.LookingCount);
        Assert.Single(supply.Results);
        Assert.Equal("10", supply.Results[0].Id);
        Assert.InRange(supply.Results[0].DistanceKm!.Value, 0, 5);
        Assert.Null(supply.Results[0].AvailableFrom);
        Assert.Null(supply.Results[0].UnlockCost);
        Assert.DoesNotContain(supply.Results[0].Features, feature => feature.Label.Contains("Parking"));

        request.ListingType = "DEMAND";
        var demand = await service.SearchPropertiesAsync(request, Guid.NewGuid());
        Assert.Single(demand.Requirements);
        Assert.Equal("20", demand.Requirements[0].Id);
        Assert.Empty(demand.Results);
    }

    private static Task CreateSchemaAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        DROP SCHEMA public CASCADE;
        CREATE SCHEMA public;

        CREATE OR REPLACE FUNCTION public.haversine_km(
            lat1 double precision, lon1 double precision,
            lat2 double precision, lon2 double precision)
        RETURNS double precision LANGUAGE sql IMMUTABLE AS $$
            SELECT 6371.0088 * 2 * asin(sqrt(
                power(sin(radians(lat2 - lat1) / 2), 2)
                + cos(radians(lat1)) * cos(radians(lat2))
                * power(sin(radians(lon2 - lon1) / 2), 2)));
        $$;

        CREATE TABLE master (masterid integer PRIMARY KEY, area text, city text, lat double precision, lng double precision);
        CREATE TABLE brokers (brokerid integer PRIMARY KEY, name text, locality text, brokerage_name text);
        CREATE TABLE listing_sizes (listingsizeid integer PRIMARY KEY, listing_id integer, size_sqft numeric, size_label text);
        CREATE TABLE listings (
            listingid integer PRIMARY KEY, broker_id integer, master_id integer, raw_message_text text,
            listing_type text, property_type text, configuration text, price numeric, price_unit text,
            size numeric, furnishing text, facing text, floor_number integer, status text, expires_at timestamptz,
            project_name text, road_info text, created_at timestamptz, updated_at timestamptz,
            last_refreshed_at timestamptz, freshness_category text, city text,
            isavailable boolean NOT NULL DEFAULT true);
        CREATE TABLE requirements (
            requirementid integer PRIMARY KEY, broker_id integer, raw_message_text text, requirement_type text,
            property_type text, configurations text[], preferred_locality_ids integer[], budget numeric,
            budget_unit text, size numeric, furnishing_pref text, facing_pref text, status text,
            expires_at timestamptz, created_at timestamptz, updated_at timestamptz,
            last_confirmed_at timestamptz, freshness_category text, city text,
            isavailable boolean NOT NULL DEFAULT true);
        """);

    private static Task SeedAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO master VALUES
            (1, 'Vijay Nagar', 'Indore', 22.7533, 75.8937),
            (2, 'Far Away', 'Indore', 22.8533, 75.9937);
        INSERT INTO brokers VALUES (1, 'Nearby Broker', 'Vijay Nagar', 'Nearby Realty');
        INSERT INTO listings VALUES
            (10, 1, 1, 'Real nearby rental', 'RENT', 'APARTMENT', '2BHK', 14000, 'MONTHLY', 950,
             'SEMI-FURNISHED', 'WEST', 2, 'ACTIVE', NULL, 'Real Project', NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', true),
            (11, 1, 2, 'Outside radius rental', 'RENT', 'APARTMENT', '2BHK', 16000, 'MONTHLY', 1000,
             NULL, NULL, NULL, 'ACTIVE', NULL, NULL, NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', true),
            (12, 1, 1, 'Nearby sale', 'SELL', 'APARTMENT', '2BHK', 5000000, 'TOTAL', 1000,
             NULL, NULL, NULL, 'ACTIVE', NULL, NULL, NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', true),
            (13, 1, 1, 'Unavailable nearby rental', 'RENT', 'APARTMENT', '2BHK', 13000, 'MONTHLY', 900,
             NULL, NULL, NULL, 'ACTIVE', NULL, NULL, NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', false);
        INSERT INTO requirements VALUES
            (20, 1, 'Real nearby rental requirement', 'RENT', 'APARTMENT', ARRAY['2BHK'], ARRAY[1], 15000,
             'MONTHLY', 900, NULL, NULL, 'ACTIVE', NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', true),
            (21, 1, 'Nearby buy requirement', 'BUY', 'APARTMENT', ARRAY['2BHK'], ARRAY[1], 5000000,
             'TOTAL', 900, NULL, NULL, 'ACTIVE', NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', true),
            (22, 1, 'Unavailable nearby rental requirement', 'RENT', 'APARTMENT', ARRAY['2BHK'], ARRAY[1], 15000,
             'MONTHLY', 900, NULL, NULL, 'ACTIVE', NULL, NOW(), NOW(), NOW(), 'FRESH', 'Indore', false);
        """);
}
