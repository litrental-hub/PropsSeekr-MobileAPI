using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PropSeekr.Controllers;
using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

public sealed class RequirementsControllerTests
{
    [Fact]
    public async Task GetMyRequirements_NormalUserUsesTheirScopedQuery()
    {
        var service = new CapturingRequirementsService();
        var userId = Guid.NewGuid();
        var controller = CreateController(userId, false, service);

        var result = await controller.GetMyRequirements(new PaginationDto { Page = 1, Limit = 20 });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(userId, service.UserId);
        Assert.False(service.AllRequirementsWasCalled);
    }

    [Fact]
    public async Task GetMyRequirements_AdminUsesUnscopedQuery()
    {
        var service = new CapturingRequirementsService();
        var controller = CreateController(Guid.NewGuid(), true, service);

        var result = await controller.GetMyRequirements(new PaginationDto { Page = 1, Limit = 20 });

        Assert.IsType<OkObjectResult>(result);
        Assert.True(service.AllRequirementsWasCalled);
        Assert.Null(service.UserId);
    }

    private static RequirementsController CreateController(Guid userId, bool isAdmin, IRequirementService service)
    {
        var controller = new RequirementsController(service, NullLogger<RequirementsController>.Instance);
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

    private sealed class CapturingRequirementsService : IRequirementService
    {
        public Guid? UserId { get; private set; }
        public bool AllRequirementsWasCalled { get; private set; }

        public Task<MyRequirementsResponseDto> GetMyRequirementsAsync(Guid userId, PaginationDto pagination, string? transactionType = null)
        {
            UserId = userId;
            return Task.FromResult(new MyRequirementsResponseDto());
        }

        public Task<MyRequirementsResponseDto> GetAllRequirementsAsync(PaginationDto pagination, string? transactionType = null)
        {
            AllRequirementsWasCalled = true;
            return Task.FromResult(new MyRequirementsResponseDto());
        }

        public Task<CreateRequirementResponseDto> AddRequirementAsync(Guid userId, CreateRequirementRequestDto request) =>
            throw new NotSupportedException();

        public Task<CreateRequirementResponseDto> UpdateRequirementAsync(Guid userId, int requirementId, CreateRequirementRequestDto request) =>
            throw new NotSupportedException();
    }
}
