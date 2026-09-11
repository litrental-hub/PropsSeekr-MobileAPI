using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using PropSeekr.Controllers;
using Xunit;

namespace PropSeekr.Tests;

public sealed class ApiSurfaceTests
{
    private static IEnumerable<(string Verb, string Path)> Routes() =>
        typeof(PaymentController).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(h => h.HttpMethods.Select(verb => (verb,
                        h.Template?.StartsWith("~/") == true ? h.Template[2..] :
                        ((t.GetCustomAttribute<RouteAttribute>()?.Template ?? "") + "/" + h.Template).TrimEnd('/'))))));

    [Fact]
    public void RetiredAndUnscopedRelationshipRoutes_AreNotExposed()
    {
        var routes = Routes().ToList();
        Assert.DoesNotContain(routes, r => r.Path.StartsWith("api/v1/payments") ||
            r.Path.StartsWith("api/v1/notifications") || r.Path.StartsWith("api/v1/property-inventory") ||
            r.Path.StartsWith("api/v1/matches/") || r.Path.Contains("/requirements/{requirementId}") ||
            r.Path == "api/v1/listings/{listingId}/requirements" || r.Path == "api/v1/listings/metrics" ||
            r.Path == "api/v1/brokers/register" ||
            r.Path == "api/v1/auth/refresh" || r.Path == "api/v1/user-matches/unlock" ||
            r.Path == "api/v1/user-matches/unlocked" || r.Path == "api/v1/user-matches/matches/{matchId}/reveal");
    }

    [Fact]
    public void CanonicalPaymentAndConsentRoutes_RemainUnique()
    {
        var routes = Routes().ToList();
        foreach (var path in new[] { "api/v1/payment/order", "api/v1/payment/verify", "api/v1/payment/webhook",
            "api/v1/user-matches/matches/{matchId}/confirm", "api/v1/user-matches/matches/{matchId}/reject" })
            Assert.Single(routes, r => r.Verb == "POST" && r.Path == path);
    }
}
