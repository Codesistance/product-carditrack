using System.Net;
using AspNetCoreRateLimit;
using CardiTrack.API.Extensions;
using CardiTrack.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// Every member-chat send runs several model calls, so both send routes carry their own limit
/// on top of the global one. AspNetCoreRateLimit matches a wildcard rule against the whole path:
/// the rule for <c>.../messages</c> does not reach <c>.../messages/stream</c>, which is how the
/// streaming send first shipped with only the global 100/min. These run the API's own
/// <c>appsettings.json</c> and rate-limit registration, so a route the rules miss fails here.
/// </summary>
public class MemberChatRateLimitTests
{
    private const string SendRule = "post:/api/v1/member-chat/members/*/messages";
    private const string StreamRule = "post:/api/v1/member-chat/members/*/messages/stream";
    private const int PerMinute = 6;

    private static readonly Lazy<IConfiguration> ApiConfiguration = new(() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepoRoot(), "src", "Presentation", "CardiTrack.API", "appsettings.json"))
            .Build());

    [Theory]
    [InlineData("messages")]
    [InlineData("messages/stream")]
    public async Task ASend_PastThePerMinuteLimit_Is429(string route)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var path = $"/api/v1/member-chat/members/{Guid.NewGuid()}/{route}";

        for (var i = 0; i < PerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, await PostAsync(client, path));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(client, path));
    }

    /// <summary>
    /// The control: a member-chat route with no rule of its own gets past the send limit, so the
    /// 429 above comes from the send rules and not from the harness.
    /// </summary>
    [Fact]
    public async Task AnUnlistedMemberChatRoute_IsNotHeldToTheSendLimit()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var path = $"/api/v1/member-chat/members/{Guid.NewGuid()}/sessions/current/end";

        for (var i = 0; i <= PerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, await PostAsync(client, path));
    }

    /// <summary>
    /// The hourly limit cannot be reached through the per-minute one inside a test, so this
    /// checks the configuration instead: the stream carries every limit the plain send does.
    /// </summary>
    [Fact]
    public void TheStream_CarriesEveryLimitOfThePlainSend()
    {
        var options = new IpRateLimitOptions();
        ApiConfiguration.Value.GetSection(ConfigurationKeys.IpRateLimiting.SectionName).Bind(options);

        var send = LimitsFor(options, SendRule);
        var stream = LimitsFor(options, StreamRule);

        Assert.Equal([("1h", 30d), ("1m", 6d)], send);
        Assert.Equal(send, stream);
    }

    private static List<(string Period, double Limit)> LimitsFor(IpRateLimitOptions options, string endpoint) =>
        options.GeneralRules
            .Where(r => string.Equals(r.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.Period, r.Limit))
            .OrderBy(r => r.Period, StringComparer.Ordinal)
            .ToList();

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(ApiConfiguration.Value);
        builder.Services.AddRateLimiting(builder.Configuration);

        var app = builder.Build();
        app.UseIpRateLimiting();
        app.Run(context => context.Response.WriteAsync("ok"));
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        // The limiter keys on the client IP; the test server supplies none, so name one the way
        // the load balancer would.
        request.Headers.Add(ApiConfiguration.Value["IpRateLimiting:RealIpHeader"]!, "203.0.113.7");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CardiTrack.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.False(dir is null, "Could not find CardiTrack.sln from the test output directory.");
        return dir!;
    }
}
