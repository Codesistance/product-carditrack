using System.Text;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Core.Offline;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Mobile;

public class AuthServiceTests
{
    private static readonly Auth0Options Options =
        new("tenant.eu.auth0.com", "client123", "https://api.carditrack.com");

    private static readonly Uri AuthorizeUri = new("https://tenant.eu.auth0.com/authorize?stub=1");

    private readonly IAuth0AuthClient _auth0 = Substitute.For<IAuth0AuthClient>();
    private readonly ITokenStore _store = Substitute.For<ITokenStore>();
    private readonly ITokenRefresher _refresher = Substitute.For<ITokenRefresher>();
    private readonly IBrowserAuthenticator _browser = Substitute.For<IBrowserAuthenticator>();

    private AuthService CreateSut() => new(_auth0, _store, _refresher, _browser, Options);

    /// <summary>An unsigned JWT with the given payload — nothing here validates signatures.</summary>
    private static string Jwt(string payloadJson) =>
        $"{Base64Url("""{"alg":"RS256","typ":"JWT"}""")}.{Base64Url(payloadJson)}.signature";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AuthTokens Tokens(string? idToken = null) =>
        new("at", "rt", idToken, DateTimeOffset.UtcNow.AddHours(1));

    private (Func<string?> Challenge, Func<string?> State) StubAuthorizeUri()
    {
        string? challenge = null, state = null;
        _auth0.BuildAuthorizeUri(
                Arg.Any<string>(),
                Arg.Do<string>(c => challenge = c),
                Arg.Do<string>(s => state = s))
            .Returns(AuthorizeUri);
        return (() => challenge, () => state);
    }

    private void StubCallback(Func<IReadOnlyDictionary<string, string>> callback) =>
        _browser.AuthenticateAsync(Arg.Any<Uri>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(_ => callback());

    // ── Password flow baseline (first tests for this class) ──

    [Fact]
    public async Task SignIn_SavesTokens_AndExposesClaims()
    {
        var tokens = Tokens(Jwt("""{"name":"Ada","email":"a@b.com"}"""));
        _auth0.LoginAsync("a@b.com", "pw", Arg.Any<CancellationToken>()).Returns(tokens);
        var sut = CreateSut();

        await sut.SignInAsync("a@b.com", "pw");

        await _store.Received(1).SaveAsync(tokens);
        Assert.Equal("Ada", sut.CurrentUserName);
        Assert.Equal("a@b.com", sut.CurrentUserEmail);
    }

    // ── Social flow ──

    [Fact]
    public async Task SocialSignIn_ExchangesCode_AndSavesTokens()
    {
        var (challenge, state) = StubAuthorizeUri();
        StubCallback(() => new Dictionary<string, string>
        {
            ["state"] = state()!,
            ["code"] = "code789",
        });
        string? verifier = null;
        var tokens = Tokens(Jwt("""{"name":"Ada","email":"a@b.com"}"""));
        _auth0.ExchangeAuthorizationCodeAsync("code789", Arg.Do<string>(v => verifier = v), Arg.Any<CancellationToken>())
            .Returns(tokens);
        var sut = CreateSut();

        await sut.SignInWithProviderAsync(Auth0Options.GoogleConnection);

        _auth0.Received(1).BuildAuthorizeUri(Auth0Options.GoogleConnection, Arg.Any<string>(), Arg.Any<string>());
        await _browser.Received(1).AuthenticateAsync(
            AuthorizeUri, new Uri(Auth0Options.CallbackUri), Arg.Any<CancellationToken>());
        // The verifier redeemed at the token endpoint must be the one whose S256 hash
        // was bound at /authorize — otherwise PKCE protects nothing.
        Assert.Equal(challenge(), Pkce.CreateChallenge(verifier!));
        await _store.Received(1).SaveAsync(tokens);
        Assert.Equal("a@b.com", sut.CurrentUserEmail);
    }

    [Fact]
    public async Task SocialSignIn_StateMismatch_Throws_WithoutExchanging()
    {
        StubAuthorizeUri();
        StubCallback(() => new Dictionary<string, string>
        {
            ["state"] = "forged",
            ["code"] = "code789",
        });
        var sut = CreateSut();

        await Assert.ThrowsAsync<AuthException>(
            () => sut.SignInWithProviderAsync(Auth0Options.GoogleConnection));

        await _auth0.DidNotReceiveWithAnyArgs().ExchangeAuthorizationCodeAsync(default!, default!);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!);
    }

    [Theory]
    [InlineData("access_denied", "email_not_verified", AuthErrorCode.EmailNotVerified)]
    [InlineData("access_denied", "blocked by policy", AuthErrorCode.Unknown)]
    [InlineData("invalid_request", "the connection was not found", AuthErrorCode.ProviderUnavailable)]
    [InlineData("server_error", "boom", AuthErrorCode.Unknown)]
    public async Task SocialSignIn_MapsCallbackErrors(string error, string description, AuthErrorCode expected)
    {
        var (_, state) = StubAuthorizeUri();
        StubCallback(() => new Dictionary<string, string>
        {
            ["state"] = state()!,
            ["error"] = error,
            ["error_description"] = description,
        });
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => sut.SignInWithProviderAsync(Auth0Options.AppleConnection));

        Assert.Equal(expected, ex.Code);
        await _auth0.DidNotReceiveWithAnyArgs().ExchangeAuthorizationCodeAsync(default!, default!);
    }

    [Fact]
    public async Task SocialSignIn_MissingCode_Throws()
    {
        var (_, state) = StubAuthorizeUri();
        StubCallback(() => new Dictionary<string, string> { ["state"] = state()! });
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => sut.SignInWithProviderAsync(Auth0Options.GoogleConnection));

        Assert.Equal(AuthErrorCode.Unknown, ex.Code);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!);
    }

    [Fact]
    public async Task SocialSignIn_BrowserCancel_Propagates_WithoutSaving()
    {
        StubAuthorizeUri();
        _browser.AuthenticateAsync(Arg.Any<Uri>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());
        var sut = CreateSut();

        // Pages absorb the cancellation silently (Fitbit precedent) — the service must
        // let it through untouched, not wrap it into an AuthException error banner.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => sut.SignInWithProviderAsync(Auth0Options.GoogleConnection));

        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!);
    }

    [Fact]
    public async Task SilentSignIn_RestoresClaims_WhenTokensAreStillInTheStore()
    {
        _store.GetAsync().Returns(Tokens(Jwt("""{"name":"Ada","email":"a@b.com"}""")));
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns("access");
        var sut = CreateSut();

        Assert.True(await sut.TrySilentSignInAsync());
        Assert.Equal("Ada", sut.CurrentUserName);
    }

    [Fact]
    public async Task SilentSignIn_StillRestoresSession_WhenRefreshMissesTheNetwork()
    {
        // TokenRefresher keeps the store on AuthErrorCode.Network; the access token it
        // returns may be expired. The session is the stored tokens, not a live bearer.
        _store.GetAsync().Returns(Tokens(Jwt("""{"name":"Ada","email":"a@b.com"}""")));
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = CreateSut();

        Assert.True(await sut.TrySilentSignInAsync());
        Assert.Equal("a@b.com", sut.CurrentUserEmail);
    }

    [Fact]
    public async Task SilentSignIn_ReturnsFalse_WhenTheStoreIsEmpty()
    {
        _store.GetAsync().Returns((AuthTokens?)null);
        _refresher.GetValidAccessTokenAsync(false, Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = CreateSut();

        Assert.False(await sut.TrySilentSignInAsync());
        Assert.Null(sut.CurrentUserEmail);
    }

    [Fact]
    public async Task SignOut_ClearsTheOfflineCache_EvenWhenRevokeThrowsUnexpectedly()
    {
        var cache = Substitute.For<IOfflineReadCache>();
        _store.GetAsync().Returns(Tokens());
        _auth0.RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("client exploded"));
        var sut = new AuthService(_auth0, _store, _refresher, _browser, Options, cache);

        await sut.SignOutAsync();

        await _store.Received(1).ClearAsync();
        await cache.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }
}
