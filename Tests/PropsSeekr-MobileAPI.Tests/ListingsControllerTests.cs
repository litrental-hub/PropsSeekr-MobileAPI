using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PropSeekr.Controllers;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

public sealed class ListingsControllerTests
{
    [Fact]
    public async Task GetMyListings_DerivesBrokerFromAuthenticatedUser()
    {
        var userId = Guid.NewGuid();
        var brokerIdentity = new StubBrokerIdentityService(42);
        var listings = new CapturingBrokerListingsService();
        await using var db = EmptyDbContext();
        var controller = Controller(db, brokerIdentity, listings, userId);

        var result = await controller.GetMyListings(2, 10, "BUY_SELL", "ACTIVE");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<GetBrokerListingsResponseDto>(ok.Value);
        Assert.Equal(42, listings.BrokerId);
        Assert.Equal(2, listings.Page);
        Assert.Equal(10, listings.Limit);
    }

    [Fact]
    public async Task GetMyListings_ReturnsNotFoundWhenAccountHasNoBrokerLink()
    {
        await using var db = EmptyDbContext();
        var controller = Controller(
            db,
            new StubBrokerIdentityService(null),
            new CapturingBrokerListingsService(),
            Guid.NewGuid());

        var result = await controller.GetMyListings();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetMyListings_RejectsInvalidPaginationBeforeQueryingDatabase()
    {
        var listings = new CapturingBrokerListingsService();
        await using var db = EmptyDbContext();
        var controller = Controller(db, new StubBrokerIdentityService(42), listings, Guid.NewGuid());

        var result = await controller.GetMyListings(page: 0, limit: 101);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(listings.BrokerId);
    }

    [Fact]
    public async Task GetMyListings_AdminUsesUnscopedQuery()
    {
        var listings = new CapturingBrokerListingsService();
        await using var db = EmptyDbContext();
        var controller = Controller(db, new StubBrokerIdentityService(42), listings, Guid.NewGuid(), isAdmin: true);

        var result = await controller.GetMyListings(1, 20);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(listings.AllListingsWasCalled);
        Assert.Null(listings.BrokerId);
    }

    [Fact]
    public async Task PatchListing_RejectsNonOwnerWithForbidden()
    {
        var listings = new CapturingBrokerListingsService();
        await using var db = EmptyDbContext();
        db.Listings.Add(new Listing { Id = 10, BrokerId = 99, PropertyType = "Apartment" });
        await db.SaveChangesAsync();

        var controller = Controller(db, new StubBrokerIdentityService(42), listings, Guid.NewGuid());
        var result = await controller.PatchListing(10, new CreateListingRequestDto { PropertyType = "Villa" });

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
    }

    private static ListingsController Controller(
        AppDbContext db,
        IBrokerIdentityService brokerIdentity,
        IBrokerListingsService listings,
        Guid userId,
        bool isAdmin = false)
    {
        var controller = new ListingsController(
            db,
            brokerIdentity,
            listings,
            new StubMatchingPipelineService(),
            NullLogger<ListingsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    isAdmin
                        ? [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Admin")]
                        : [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                    "test"))
            }
        };
        return controller;
    }

    private static AppDbContext EmptyDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class StubBrokerIdentityService(int? brokerId) : IBrokerIdentityService
    {
        public Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(brokerId);

        public Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingBrokerListingsService : IBrokerListingsService
    {
        public int? BrokerId { get; private set; }
        public bool AllListingsWasCalled { get; private set; }
        public int Page { get; private set; }
        public int Limit { get; private set; }

        public Task<GetBrokerListingsResponseDto> GetAllListingsAsync(
            int page,
            int limit,
            string? transactionType = null,
            string? status = null,
            CancellationToken cancellationToken = default)
        {
            AllListingsWasCalled = true;
            return Task.FromResult(new GetBrokerListingsResponseDto { Page = page, Limit = limit });
        }

        public Task<GetBrokerListingsResponseDto> GetMyListingsAsync(
            int brokerId,
            int page,
            int limit,
            string? transactionType = null,
            string? status = null,
            CancellationToken cancellationToken = default)
        {
            BrokerId = brokerId;
            Page = page;
            Limit = limit;
            return Task.FromResult(new GetBrokerListingsResponseDto
            {
                Page = page,
                Limit = limit
            });
        }
    }

    private sealed class StubMatchingPipelineService : IMatchingPipelineService
    {
        public Task TriggerForListingAsync(
            int listingId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TriggerForRequirementAsync(
            int requirementId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
