using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PropSeekr.Attributes;
using Xunit;

namespace PropSeekr.Tests;

public class InternalEndpointSecurityTests
{
    [Fact]
    public async Task OnActionExecutionAsync_WhenApiKeyConfigured_AndHeaderMissing_Returns401()
    {
        // Arrange
        var inMemoryConfig = new Dictionary<string, string?>
        {
            { RequireInternalServiceKeyAttribute.ConfigKey, "secret-test-key-123" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        var filter = new RequireInternalServiceKeyAttribute();
        var wasNextCalled = false;

        // Act
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            wasNextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        // Assert
        Assert.False(wasNextCalled);
        var result = Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WhenApiKeyConfigured_AndHeaderInvalid_Returns401()
    {
        // Arrange
        var inMemoryConfig = new Dictionary<string, string?>
        {
            { RequireInternalServiceKeyAttribute.ConfigKey, "secret-test-key-123" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        httpContext.Request.Headers[RequireInternalServiceKeyAttribute.HeaderName] = "wrong-key";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        var filter = new RequireInternalServiceKeyAttribute();
        var wasNextCalled = false;

        // Act
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            wasNextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        // Assert
        Assert.False(wasNextCalled);
        var result = Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WhenApiKeyConfigured_AndHeaderMatches_CallsNext()
    {
        // Arrange
        const string validKey = "secret-test-key-123";
        var inMemoryConfig = new Dictionary<string, string?>
        {
            { RequireInternalServiceKeyAttribute.ConfigKey, validKey }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };
        httpContext.Request.Headers[RequireInternalServiceKeyAttribute.HeaderName] = validKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        var filter = new RequireInternalServiceKeyAttribute();
        var wasNextCalled = false;

        // Act
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            wasNextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        // Assert
        Assert.True(wasNextCalled);
        Assert.Null(executingContext.Result);
    }
}

