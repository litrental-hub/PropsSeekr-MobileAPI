using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class Msg91WidgetVerifierTests
{
    private static readonly DateTime Started = DateTime.UtcNow.AddSeconds(-10);

    [Fact]
    public async Task ValidatesWithBackendKeyAndExactProviderIdentifier()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"type":"success","message":"919876543210"}""");
        await Create(handler).VerifyAsync(Token(), "919876543210", Started);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.msg91.com/api/v5/widget/verifyAccessToken", handler.Url);
        Assert.Equal("backend-test-key", handler.AuthKey);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(Token(), body.RootElement.GetProperty("access-token").GetString());
        Assert.DoesNotContain("backend-test-key", handler.Body);
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"message\":\"919876543210\"}")]
    [InlineData("{\"type\":\"success\",\"message\":\"919876543211\"}")]
    [InlineData("{\"type\":\"success\",\"message\":\"broker@example.test\"}")]
    [InlineData("{\"type\":\"success\",\"message\":\"449876543210\"}")]
    [InlineData("{\"type\":\"success\"}")]
    [InlineData("{\"type\":true,\"message\":919876543210}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task RejectsApplicationErrorsMalformedResponsesAndWrongIdentity(string response)
    {
        var verifier = Create(new StubHandler(HttpStatusCode.OK, response));
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.VerifyAsync(Token(), "919876543210", Started));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RejectsHttpFailuresWithoutLeakingProviderBody(HttpStatusCode status)
    {
        var verifier = Create(new StubHandler(status, "secret-provider-response"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.VerifyAsync(Token(), "919876543210", Started));
        Assert.DoesNotContain("secret-provider-response", error.ToString());
    }

    [Theory]
    [InlineData(-60, 300)] // issued before our challenge
    [InlineData(120, 300)] // future-issued
    [InlineData(-5, -1)] // expired
    public async Task RejectsStaleExpiredOrFutureProof(int issuedOffset, int expiryOffset)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new StubHandler(HttpStatusCode.OK, """{"type":"success","message":"919876543210"}"""))
                .VerifyAsync(Token(issuedOffset, expiryOffset), "919876543210", Started));
    }

    [Fact]
    public async Task MissingBackendKeyFailsBeforeNetworkCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var verifier = new Msg91WidgetVerifier(new HttpClient(handler), new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.VerifyAsync(Token(), "919876543210", Started));
        Assert.Null(handler.Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"iat\":\"invalid\",\"exp\":9999999999}")]
    [InlineData("{\"iat\":9999999999}")]
    public async Task RejectsMissingOrMalformedTokenTimestamps(string payload)
    {
        var token = Base64UrlEncoder.Encode("{\"alg\":\"HS256\"}") + "." + Base64UrlEncoder.Encode(payload) + ".dGVzdA";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new StubHandler(HttpStatusCode.OK, """{"type":"success","message":"919876543210"}"""))
                .VerifyAsync(token, "919876543210", Started));
    }

    private static Msg91WidgetVerifier Create(StubHandler handler) => new(new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Msg91:AuthKey"] = "backend-test-key" }).Build());

    private static string Token(int issuedOffset = -5, int expiryOffset = 300)
    {
        var payload = new JwtPayload
        {
            ["iat"] = new DateTimeOffset(Started.AddSeconds(10 + issuedOffset)).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(Started.AddSeconds(10 + expiryOffset)).ToUnixTimeSeconds()
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(), payload));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? AuthKey { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method; Url = request.RequestUri!.ToString();
            AuthKey = request.Headers.GetValues("authkey").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
