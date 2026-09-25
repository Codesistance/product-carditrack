using System.Net;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Shared;
using CardiTrack.UnitTests.Mobile;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class Auth0ManagementClientTests
{
    private const string TokenJson = """{"access_token":"mgmt-token","expires_in":3600}""";

    private static (Auth0ManagementClient Client, FakeHttpMessageHandler Http) CreateSut(
        ILogger<Auth0ManagementClient>? logger = null)
    {
        var http = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Auth0Client").Returns(
            new HttpClient(http) { BaseAddress = new Uri("https://tenant.eu.auth0.com/") });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth0:Domain"] = "tenant.eu.auth0.com",
                ["Auth0:ClientId"] = "web-client",
                ["Auth0:ClientSecret"] = "web-secret",
            })
            .Build();

        var client = new Auth0ManagementClient(
            factory,
            new ConfigurationLoader(configuration),
            new MemoryCache(new MemoryCacheOptions()),
            logger ?? NullLogger<Auth0ManagementClient>.Instance);
        return (client, http);
    }

    private const string BodySentinel = "provider-free-text-sentinel";

    /// <summary>
    /// A token or user body that fails to parse is reported by position only: the JSON path is
    /// built from the body's property names and the reader's text quotes tokens, and a token body
    /// can hold a live credential.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resend_LogsParseFailuresByPositionOnly(bool tokenBodyIsMalformed)
    {
        var logger = Substitute.For<ILogger<Auth0ManagementClient>>();
        var (client, http) = CreateSut(logger);
        const string malformed = "{ \"" + BodySentinel + "\": tru }";
        if (tokenBodyIsMalformed)
            http.Enqueue(HttpStatusCode.OK, malformed);
        else
            http.Enqueue(HttpStatusCode.OK, TokenJson).Enqueue(HttpStatusCode.OK, malformed);

        await client.TrySendVerificationEmailAsync("a@b.com");

        var warnings = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Select(c => c.GetArguments()[2]!.ToString()!)
            .ToList();
        Assert.Contains(warnings, w => w.Contains("line 1, pos"));
        Assert.All(warnings, w => Assert.DoesNotContain(BodySentinel, w));
    }

    [Fact]
    public async Task Resend_FetchesToken_LooksUpUser_AndCreatesJob()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.OK, """[{"user_id":"auth0|abc","email":"a@b.com"}]""")
            .Enqueue(HttpStatusCode.Created, """{"status":"pending"}""");

        await client.TrySendVerificationEmailAsync("A@B.com");

        Assert.Equal(3, http.Requests.Count);
        Assert.Contains("client_credentials", http.Requests[0].Body);
        Assert.Equal("/api/v2/users-by-email", http.Requests[1].Uri!.AbsolutePath);
        Assert.Contains("a%40b.com", http.Requests[1].Uri!.Query); // lowercased + escaped
        Assert.Equal("Bearer mgmt-token", http.Requests[1].AuthHeader);
        Assert.Equal("/api/v2/jobs/verification-email", http.Requests[2].Uri!.AbsolutePath);
        Assert.Contains("auth0|abc", http.Requests[2].Body);
    }

    [Fact]
    public async Task Resend_UnknownEmail_SkipsTheJob()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.OK, "[]");

        await client.TrySendVerificationEmailAsync("nobody@b.com");

        Assert.Equal(2, http.Requests.Count); // no jobs call
    }

    [Fact]
    public async Task Resend_TokenFailure_NeverThrows()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Unauthorized,
            """{"error":"access_denied","error_description":"Unauthorized"}""");

        await client.TrySendVerificationEmailAsync("a@b.com");

        Assert.Single(http.Requests); // stopped after the failed token request
    }

    [Fact]
    public async Task Resend_CachesTheManagementToken()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.OK, "[]")
            .Enqueue(HttpStatusCode.OK, "[]");

        await client.TrySendVerificationEmailAsync("a@b.com");
        await client.TrySendVerificationEmailAsync("b@b.com");

        // 1 token request + 2 lookups: the second call reused the cached token
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal("/api/v2/users-by-email", http.Requests[2].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Delete_RemovesTheUser()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.NoContent, "");

        await client.TryDeleteUserAsync("auth0|abc");

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(HttpMethod.Delete, http.Requests[1].Method);
        Assert.Equal("/api/v2/users/auth0%7Cabc", http.Requests[1].Uri!.AbsolutePath);
        Assert.Equal("Bearer mgmt-token", http.Requests[1].AuthHeader);
    }

    [Fact]
    public async Task Delete_TreatsNotFoundAsAlreadyGone()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.NotFound, """{"statusCode":404}""");

        await client.TryDeleteUserAsync("auth0|abc");

        Assert.Equal(2, http.Requests.Count);
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Delete_BlocksWhenDeleteIsRefused()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson)
            .Enqueue(HttpStatusCode.Forbidden, """{"statusCode":403}""")
            .Enqueue(HttpStatusCode.OK, """{"user_id":"auth0|abc","blocked":true}""");

        await client.TryDeleteUserAsync("auth0|abc");

        Assert.Equal(3, http.Requests.Count);
        Assert.Equal(HttpMethod.Patch, http.Requests[2].Method);
        Assert.Contains("\"blocked\":true", http.Requests[2].Body);
    }

    [Fact]
    public async Task Delete_NeverThrows()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");

        await client.TryDeleteUserAsync("auth0|abc");

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Delete_SkipsABlankId()
    {
        var (client, http) = CreateSut();

        await client.TryDeleteUserAsync("  ");

        Assert.Empty(http.Requests);
    }
}
