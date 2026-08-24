using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class BrokerListingMapperTests
{
    [Theory]
    [InlineData("RENT", "RENTAL")]
    [InlineData("lease", "RENTAL")]
    [InlineData("SELL", "BUY_SELL")]
    [InlineData("SUPPLY", "BUY_SELL")]
    [InlineData(null, "BUY_SELL")]
    public void NormalizeTransactionType_ProducesUiTransaction(string? raw, string expected)
    {
        Assert.Equal(expected, BrokerListingsService.NormalizeTransactionType(raw));
    }

    [Fact]
    public void BuildTitle_UsesSizeLabelWhenConfigurationIsMissing()
    {
        var sizes = new List<BrokerListingSizeDto>
        {
            new() { Label = "2 BHK", SizeSqft = 1100 }
        };

        Assert.Equal("2 BHK Apartment", BrokerListingsService.BuildTitle("", "Apartment", sizes));
    }

    [Fact]
    public void BuildTitle_FallsBackForSparseLegacyListings()
    {
        Assert.Equal("Property Listing", BrokerListingsService.BuildTitle("", "", []));
    }
}

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class BrokerListingsServiceIntegrationTests : IAsyncLifetime
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
    public async Task GetMyListings_ReturnsOnlyOwnedListingsWithBatchedCardData()
    {
        Assert.NotNull(_db);
        var service = new BrokerListingsService(_db!);

        var response = await service.GetMyListingsAsync(1, 1, 20);

        Assert.True(response.Success);
        Assert.Equal(2, response.TotalCount);
        Assert.Equal([11, 10], response.Data.Select(item => item.ListingId));

        var sale = response.Data.Single(item => item.ListingId == 10);
        Assert.Equal("2 BHK APARTMENT", sale.Title);
        Assert.Equal("BUY/SELL", sale.Type);
        Assert.Equal("BUY_SELL", sale.TransactionType);
        Assert.Equal("Vijay Nagar", sale.Location);
        Assert.Equal(2, sale.MatchCount);
        Assert.Equal(1100, sale.BuiltUpSize);
        Assert.Null(sale.Views);

        Assert.DoesNotContain(response.Data, item => item.ListingId == 20);
    }

    [PostgreSqlFact]
    public async Task GetMyListings_AppliesTransactionFilterAndKeepsEmptyPagesHonest()
    {
        Assert.NotNull(_db);
        var service = new BrokerListingsService(_db!);

        var rental = await service.GetMyListingsAsync(1, 1, 20, "RENTAL");
        Assert.Single(rental.Data);
        Assert.Equal(11, rental.Data[0].ListingId);

        var emptyPage = await service.GetMyListingsAsync(1, 2, 20);
        Assert.Equal(2, emptyPage.TotalCount);
        Assert.Empty(emptyPage.Data);
    }

    private static Task CreateSchemaAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        DROP SCHEMA public CASCADE;
        CREATE SCHEMA public;

        CREATE TABLE listings (
            listingid integer PRIMARY KEY,
            broker_id integer NOT NULL,
            listing_type text,
            property_type text,
            configuration text,
            price numeric,
            price_unit text,
            size numeric,
            status text,
            project_name text,
            city text,
            created_at timestamptz,
            updated_at timestamptz
        );

        CREATE TABLE listing_sizes (
            listingsizeid integer PRIMARY KEY,
            listing_id integer NOT NULL,
            size_sqft numeric NOT NULL,
            size_label text
        );

        CREATE TABLE matches (
            matchid integer PRIMARY KEY,
            listing_id integer NOT NULL
        );
        """);

    private static Task SeedAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO listings (
            listingid, broker_id, listing_type, property_type, configuration,
            price, price_unit, size, status, project_name, city, created_at, updated_at)
        VALUES
            (10, 1, 'SELL', 'APARTMENT', NULL, 5500000, 'INR', NULL, 'active', 'Vijay Nagar', 'Indore', NOW() - INTERVAL '1 day', NOW()),
            (11, 1, 'RENT', 'OFFICE', 'Commercial', 85000, 'INR', 1500, 'active', NULL, 'Indore', NOW(), NOW()),
            (20, 2, 'SELL', 'APARTMENT', '3 BHK', 7500000, 'INR', 1400, 'active', 'Palasia', 'Indore', NOW(), NOW());

        INSERT INTO listing_sizes (listingsizeid, listing_id, size_sqft, size_label)
        VALUES (1, 10, 1100, '2 BHK'), (2, 20, 1400, '3 BHK');

        INSERT INTO matches (matchid, listing_id)
        VALUES (100, 10), (101, 10), (102, 20);
        """);
}
