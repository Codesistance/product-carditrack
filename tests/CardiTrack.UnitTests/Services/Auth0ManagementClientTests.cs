using System.Net;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Shared;
using CardiTrack.UnitTests.Mobile;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class Auth0ManagementClientTests
{
    private const string TokenJson = """{"access_token":"mgmt-token","expires_in":3600}""";

    private static (Auth0ManagementClient Client, FakeHttpMessageHandler Http) CreateSut()
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
            NullLogger<Auth0ManagementClient>.Instance);
        return (client, http);
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
}
