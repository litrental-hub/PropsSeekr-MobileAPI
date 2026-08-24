using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PropSeekr.Controllers;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

public sealed class UserMatchesControllerTests
{
    [Fact]
    public async Task GetUserMatches_ForwardsRequirementIdWithoutUsingListingId()
    {
        var userId = Guid.NewGuid();
        var matchesService = new CapturingUserMatchesService();
        var controller = CreateController(userId, matchesService);

        var result = await controller.GetUserMatches(
            type: null,
            transactionType: "BUY_SELL",
            listingId: null,
            requirementId: 25186,
            matchId: null,
            page: 1,
            limit: 20);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(matchesService.ListingId);
        Assert.Equal(25186, matchesService.RequirementId);
        Assert.Equal("BUY_SELL", matchesService.TransactionType);
    }

    [Fact]
    public async Task GetUserMatches_RejectsInvalidRequirementId()
    {
        var matchesService = new CapturingUserMatchesService();
        var controller = CreateController(Guid.NewGuid(), matchesService);

        var result = await controller.GetUserMatches(
            type: null,
            transactionType: null,
            listingId: null,
            requirementId: 0,
            matchId: null,
            page: 1,
            limit: 20);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(matchesService.WasCalled);
    }

    [Fact]
    public async Task RejectMatch_UsesAuthenticatedBrokerAndStructuredReason()
    {
        var unlockService = new CapturingUnlockService();
        var controller = CreateController(Guid.NewGuid(), new CapturingUserMatchesService(), unlockService);
        var request = new MatchRejectionRequestDto
        {
            MatchId = 500,
            ConnectionRequestId = 91,
            ReasonCode = "PRICE_CHANGED"
        };

        var result = await controller.RejectMatch(500, request);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, unlockService.BrokerId);
        Assert.Same(request, unlockService.Request);
    }

    private static UserMatchesController CreateController(
        Guid userId,
        IUserMatchesService matchesService,
        IUnlockService? unlockService = null)
    {
        var controller = new UserMatchesController(
            matchesService,
            unlockService ?? new UnusedUnlockService(),
            NullLogger<UserMatchesController>.Instance,
            new FixedBrokerIdentityService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                    authenticationType: "test"))
            }
        };
        return controller;
    }

    private sealed class CapturingUserMatchesService : IUserMatchesService
    {
        public bool WasCalled { get; private set; }
        public string? TransactionType { get; private set; }
        public int? ListingId { get; private set; }
        public int? RequirementId { get; private set; }

        public Task<UserMatchesResponseDto> GetUserMatchesAsync(
            Guid userId,
            string? transactionType = null,
            int? listingId = null,
            int? requirementId = null,
            int? matchId = null,
            int page = 1,
            int limit = 20)
        {
            WasCalled = true;
            TransactionType = transactionType;
            ListingId = listingId;
            RequirementId = requirementId;
            return Task.FromResult(new UserMatchesResponseDto());
        }

        public Task<UnlockPropertyResponseDto> UnlockPropertyAsync(Guid userId, UnlockPropertyRequestDto request) =>
            throw new NotSupportedException();

        public Task<UserMatchesResponseDto> GetUnlockedPropertiesAsync(Guid userId, int page = 1, int limit = 20) =>
            throw new NotSupportedException();
    }

    private sealed class FixedBrokerIdentityService : IBrokerIdentityService
    {
        public Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(1);

        public Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class UnusedUnlockService : IUnlockService
    {
        public Task<MatchConfirmationResponseDto> ConfirmMatchAsync(int brokerId, MatchConfirmationRequestDto request) =>
            throw new NotSupportedException();

        public Task<UnlockPropertyResponseDto> UnlockMatchAsync(int brokerId, UnlockPropertyRequestDto request) =>
            throw new NotSupportedException();

        public Task<MatchRejectionResponseDto> RejectMatchAsync(int brokerId, MatchRejectionRequestDto request) =>
            throw new NotSupportedException();

        public Task<bool> IsMatchRevealedAsync(int matchId, Guid userId) =>
            throw new NotSupportedException();

        public Task<CreditWallet> GetWalletAsync(Guid userId) =>
            throw new NotSupportedException();

        public Task InitializeWalletAsync(Guid userId, int freeCredits = 5) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingUnlockService : IUnlockService
    {
        public int? BrokerId { get; private set; }
        public MatchRejectionRequestDto? Request { get; private set; }

        public Task<MatchRejectionResponseDto> RejectMatchAsync(int brokerId, MatchRejectionRequestDto request)
        {
            BrokerId = brokerId;
            Request = request;
            return Task.FromResult(new MatchRejectionResponseDto
            {
                Success = true,
                MatchId = request.MatchId,
                ConnectionRequestId = request.ConnectionRequestId ?? 1
            });
        }

        public Task<MatchConfirmationResponseDto> ConfirmMatchAsync(int brokerId, MatchConfirmationRequestDto request) => throw new NotSupportedException();
        public Task<UnlockPropertyResponseDto> UnlockMatchAsync(int brokerId, UnlockPropertyRequestDto request) => throw new NotSupportedException();
        public Task<bool> IsMatchRevealedAsync(int matchId, Guid userId) => throw new NotSupportedException();
        public Task<CreditWallet> GetWalletAsync(Guid userId) => throw new NotSupportedException();
        public Task InitializeWalletAsync(Guid userId, int freeCredits = 5) => throw new NotSupportedException();
    }
}
