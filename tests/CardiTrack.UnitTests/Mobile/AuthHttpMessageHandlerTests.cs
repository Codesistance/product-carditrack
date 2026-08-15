using System.Net;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Http;
using NSubstitute;

namespace CardiTrack.UnitTests.Mobile;

public class AuthHttpMessageHandlerTests
{
    private readonly ITokenRefresher _refresher = Substitute.For<ITokenRefresher>();

    private (HttpClient Client, FakeHttpMessageHandler Inner) CreateSut()
    {
        var inner = new FakeHttpMessageHandler();
        var handler = new AuthHttpMessageHandler(_refresher) { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, inner);
    }

    [Fact]
    public async Task AttachesBearerToken()
    {
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("token-1");
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.OK, "{}");

        await client.GetAsync("/x");

        Assert.Equal("Bearer token-1", inner.Requests.Single().AuthHeader);
    }

    [Fact]
    public async Task SendsWithoutHeader_WhenSignedOut()
    {
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns((string?)null);
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(inner.Requests.Single().AuthHeader);
        await _refresher.DidNotReceive().GetValidAccessTokenAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetriesOnceWithFreshToken_After401()
    {
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("stale");
        _refresher.GetValidAccessTokenAsync(true, Arg.Any<CancellationToken>()).Returns("fresh");
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.Unauthorized, "{}").Enqueue(HttpStatusCode.OK, "{}");

        var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal("Bearer fresh", inner.Requests[1].AuthHeader);
    }

    [Fact]
    public async Task DoesNotLoop_When401PersistsAfterRetry()
    {
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("stale");
        _refresher.GetValidAccessTokenAsync(true, Arg.Any<CancellationToken>()).Returns("fresh");
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.Unauthorized, "{}").Enqueue(HttpStatusCode.Unauthorized, "{}");

        var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task ReturnsOriginal401_WhenRefreshFails()
    {
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("stale");
        _refresher.GetValidAccessTokenAsync(true, Arg.Any<CancellationToken>()).Returns((string?)null);
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task DoesNotRetry_WhenRefreshReturnsTheSameToken()
    {
        // Offline refresh hands back the stored bearer rather than null. Retrying with it
        // would just 401 again.
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("stale");
        _refresher.GetValidAccessTokenAsync(true, Arg.Any<CancellationToken>()).Returns("stale");
        var (client, inner) = CreateSut();
        inner.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(inner.Requests);
    }
}
