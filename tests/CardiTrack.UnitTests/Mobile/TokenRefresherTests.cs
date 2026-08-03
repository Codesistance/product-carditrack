using CardiTrack.Mobile.Core.Auth;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Mobile;

public class TokenRefresherTests
{
    private readonly ITokenStore _store = Substitute.For<ITokenStore>();
    private readonly IAuth0AuthClient _auth0 = Substitute.For<IAuth0AuthClient>();

    private TokenRefresher CreateSut() => new(_store, _auth0);

    private static AuthTokens Valid(string access = "access-1", string? refresh = "refresh-1") =>
        new(access, refresh, "id-1", DateTimeOffset.UtcNow.AddMinutes(30));

    private static AuthTokens Expired(string access = "access-0", string? refresh = "refresh-0") =>
        new(access, refresh, "id-0", DateTimeOffset.UtcNow.AddMinutes(-5));

    [Fact]
    public async Task ReturnsNull_WhenNoStoredTokens()
    {
        _store.GetAsync().Returns((AuthTokens?)null);

        var sut = CreateSut();

        Assert.Null(await sut.GetValidAccessTokenAsync());
        await _auth0.DidNotReceiveWithAnyArgs().RefreshAsync(default!, default);
    }

    [Fact]
    public async Task ReturnsAccessToken_WithoutRefresh_WhenStillValid()
    {
        _store.GetAsync().Returns(Valid());

        var sut = CreateSut();

        Assert.Equal("access-1", await sut.GetValidAccessTokenAsync());
        await _auth0.DidNotReceiveWithAnyArgs().RefreshAsync(default!, default);
    }

    [Fact]
    public async Task Refreshes_WhenExpired_AndSavesNewTokens()
    {
        _store.GetAsync().Returns(Expired());
        _auth0.RefreshAsync("refresh-0", Arg.Any<CancellationToken>())
            .Returns(new AuthTokens("access-2", "refresh-2", "id-2", DateTimeOffset.UtcNow.AddMinutes(30)));

        var sut = CreateSut();

        Assert.Equal("access-2", await sut.GetValidAccessTokenAsync());
        await _store.Received(1).SaveAsync(Arg.Is<AuthTokens>(t =>
            t != null && t.AccessToken == "access-2" && t.RefreshToken == "refresh-2"));
    }

    [Fact]
    public async Task KeepsOldRefreshToken_WhenRotationOmitsNewOne()
    {
        _store.GetAsync().Returns(Expired());
        _auth0.RefreshAsync("refresh-0", Arg.Any<CancellationToken>())
            .Returns(new AuthTokens("access-2", null, "id-2", DateTimeOffset.UtcNow.AddMinutes(30)));

        var sut = CreateSut();
        await sut.GetValidAccessTokenAsync();

        await _store.Received(1).SaveAsync(Arg.Is<AuthTokens>(t => t != null && t.RefreshToken == "refresh-0"));
    }

    [Fact]
    public async Task ClearsSessionAndRaisesEvent_WhenRefreshTokenRejected()
    {
        _store.GetAsync().Returns(Expired());
        _auth0.RefreshAsync("refresh-0", Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthException(AuthErrorCode.InvalidCredentials, "invalid_grant"));

        var sut = CreateSut();
        var expired = false;
        sut.SessionExpired += () => expired = true;

        Assert.Null(await sut.GetValidAccessTokenAsync());
        Assert.True(expired);
        await _store.Received(1).ClearAsync();
    }

    [Fact]
    public async Task KeepsSession_WhenRefreshFailsWithNetworkError()
    {
        _store.GetAsync().Returns(Expired());
        _auth0.RefreshAsync("refresh-0", Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthException(AuthErrorCode.Network, "offline"));

        var sut = CreateSut();
        var expired = false;
        sut.SessionExpired += () => expired = true;

        Assert.Null(await sut.GetValidAccessTokenAsync());
        Assert.False(expired);
        await _store.DidNotReceive().ClearAsync();
    }

    [Fact]
    public async Task ClearsSession_WhenExpiredAndNoRefreshToken()
    {
        _store.GetAsync().Returns(Expired(refresh: null));

        var sut = CreateSut();

        Assert.Null(await sut.GetValidAccessTokenAsync());
        await _store.Received(1).ClearAsync();
    }

    [Fact]
    public async Task ConcurrentForceRefreshCalls_RefreshOnlyOnce()
    {
        var stored = Expired();
        _store.GetAsync().Returns(_ => stored);
        _store.SaveAsync(Arg.Do<AuthTokens>(t => stored = t)).Returns(Task.CompletedTask);

        var refreshCalls = 0;
        _auth0.RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(50);
                return new AuthTokens("access-2", "refresh-2", "id-2", DateTimeOffset.UtcNow.AddMinutes(30));
            });

        var sut = CreateSut();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => sut.GetValidAccessTokenAsync(forceRefresh: true)));

        Assert.All(results, r => Assert.Equal("access-2", r));
        Assert.Equal(1, refreshCalls);
    }
}
