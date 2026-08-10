using System.Net;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;

namespace CardiTrack.UnitTests.Mobile;

public class Auth0AuthClientTests
{
    private static readonly Auth0Options Options =
        new("tenant.eu.auth0.com", "client123", "https://api.carditrack.com");

    private static (Auth0AuthClient Client, FakeHttpMessageHandler Http) CreateSut(Auth0Options? options = null)
    {
        var http = new FakeHttpMessageHandler();
        var client = new Auth0AuthClient(
            new HttpClient(http) { BaseAddress = new Uri("https://tenant.eu.auth0.com") },
            options ?? Options);
        return (client, http);
    }

    private const string TokenJson = """
        {"access_token":"at","refresh_token":"rt","id_token":"it","expires_in":3600,"token_type":"Bearer"}
        """;

    [Fact]
    public async Task Login_SendsPasswordRealmGrant_AndParsesTokens()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson);

        var tokens = await client.LoginAsync("a@b.com", "pw");

        var body = http.Requests.Single().Body!;
        Assert.Contains("grant_type=http%3A%2F%2Fauth0.com%2Foauth%2Fgrant-type%2Fpassword-realm", body);
        Assert.Contains("realm=Username-Password-Authentication", body);
        Assert.Contains("client_id=client123", body);
        Assert.Contains("username=a%40b.com", body);
        Assert.Contains("offline_access", body);
        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Login_MapsInvalidGrant_ToInvalidCredentials()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Forbidden,
            """{"error":"invalid_grant","error_description":"Wrong email or password."}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "bad"));
        Assert.Equal(AuthErrorCode.InvalidCredentials, ex.Code);
    }

    [Theory]
    [InlineData("email_not_verified")]                          // canonical (runbook Action)
    [InlineData("Please verify your email before logging in.")] // prose fallback
    public async Task Login_MapsVerificationDeny_ToEmailNotVerified(string description)
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Forbidden,
            $$"""{"error":"access_denied","error_description":"{{description}}"}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.EmailNotVerified, ex.Code);
    }

    [Fact]
    public async Task Login_OtherAccessDenied_StaysUnknown()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Forbidden,
            """{"error":"access_denied","error_description":"blocked by policy"}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.Unknown, ex.Code);
    }

    [Fact]
    public async Task Login_MapsTooManyAttempts()
    {
        var (client, http) = CreateSut();
        http.Enqueue((HttpStatusCode)429,
            """{"error":"too_many_attempts","error_description":"blocked"}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.TooManyAttempts, ex.Code);
    }

    [Fact]
    public async Task Login_MapsTransportFailure_ToNetwork()
    {
        var (client, http) = CreateSut();
        http.Throws(new HttpRequestException("dns"));

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.Network, ex.Code);
    }

    [Fact]
    public async Task Login_Throws_WhenNotConfigured()
    {
        var (client, _) = CreateSut(new Auth0Options("", "", ""));

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.LoginAsync("a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.NotConfigured, ex.Code);
    }

    [Fact]
    public async Task SignUp_PostsJsonPayload()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, """{"_id":"1","email":"a@b.com"}""");

        await client.SignUpAsync("Ada", "a@b.com", "pw");

        var request = http.Requests.Single();
        Assert.Equal("/dbconnections/signup", request.Uri!.AbsolutePath);
        Assert.Contains("\"client_id\":\"client123\"", request.Body);
        Assert.Contains("\"connection\":\"Username-Password-Authentication\"", request.Body);
        Assert.Contains("\"name\":\"Ada\"", request.Body);
    }

    [Fact]
    public async Task SignUp_MapsUserExists()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.BadRequest, """{"code":"user_exists","description":"The user already exists."}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.SignUpAsync("Ada", "a@b.com", "pw"));
        Assert.Equal(AuthErrorCode.UserAlreadyExists, ex.Code);
    }

    [Fact]
    public async Task SignUp_MapsPasswordStrengthError_ToWeakPassword()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.BadRequest,
            """{"name":"PasswordStrengthError","message":"Password is too weak","policy":"* At least 8 characters"}""");

        var ex = await Assert.ThrowsAsync<AuthException>(() => client.SignUpAsync("Ada", "a@b.com", "weak"));
        Assert.Equal(AuthErrorCode.WeakPassword, ex.Code);
        Assert.Contains("At least 8 characters", ex.Auth0Description);
    }

    [Fact]
    public async Task PasswordReset_TreatsAny2xxAsSuccess()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, "We've just sent you an email to reset your password.", "text/html");

        await client.RequestPasswordResetAsync("a@b.com");

        Assert.Equal("/dbconnections/change_password", http.Requests.Single().Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Refresh_SendsRefreshGrant()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson);

        await client.RefreshAsync("rt-old");

        var body = http.Requests.Single().Body!;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=rt-old", body);
    }

    [Fact]
    public async Task Refresh_SendsNoAudience()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson);

        await client.RefreshAsync("rt-old");

        // Auth0's refresh_token grant takes no audience — the refresh token is already bound
        // to the audience and scope granted at login. Sending one looks like a fix for an
        // audience mismatch while doing nothing; the real fault is the login-time config.
        Assert.DoesNotContain("audience", http.Requests.Single().Body!);
    }

    [Fact]
    public void BuildAuthorizeUri_ContainsAllParameters()
    {
        var (client, _) = CreateSut();

        var uri = client.BuildAuthorizeUri(Auth0Options.GoogleConnection, "chal123", "state456");

        Assert.Equal("tenant.eu.auth0.com", uri.Host);
        Assert.Equal("/authorize", uri.AbsolutePath);
        var query = uri.Query;
        Assert.Contains("response_type=code", query);
        Assert.Contains("client_id=client123", query);
        Assert.Contains("connection=google-oauth2", query);
        Assert.Contains("redirect_uri=carditrack%3A%2F%2Foauth%2Fcallback", query);
        Assert.Contains("audience=https%3A%2F%2Fapi.carditrack.com", query);
        Assert.Contains("scope=openid%20profile%20email%20offline_access", query);
        Assert.Contains("code_challenge=chal123", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("state=state456", query);
    }

    [Fact]
    public void BuildAuthorizeUri_Throws_WhenNotConfigured()
    {
        var (client, _) = CreateSut(new Auth0Options("", "", ""));

        var ex = Assert.Throws<AuthException>(
            () => client.BuildAuthorizeUri(Auth0Options.GoogleConnection, "c", "s"));
        Assert.Equal(AuthErrorCode.NotConfigured, ex.Code);
    }

    [Fact]
    public async Task ExchangeAuthorizationCode_PostsExpectedForm()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.OK, TokenJson);

        var tokens = await client.ExchangeAuthorizationCodeAsync("code789", "verifier000");

        var request = http.Requests.Single();
        Assert.Equal("/oauth/token", request.Uri!.AbsolutePath);
        var body = request.Body!;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("client_id=client123", body);
        Assert.Contains("code=code789", body);
        Assert.Contains("code_verifier=verifier000", body);
        Assert.Contains("redirect_uri=carditrack%3A%2F%2Foauth%2Fcallback", body);
        // Audience and scope were bound at /authorize — the exchange must not repeat them.
        Assert.DoesNotContain("audience", body);
        Assert.DoesNotContain("scope", body);
        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
    }

    [Fact]
    public async Task ExchangeAuthorizationCode_MapsInvalidGrant_ToExpiredCodeMessage()
    {
        var (client, http) = CreateSut();
        http.Enqueue(HttpStatusCode.Forbidden,
            """{"error":"invalid_grant","error_description":"Invalid authorization code"}""");

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.ExchangeAuthorizationCodeAsync("stale", "verifier"));

        // invalid_grant here means an expired/consumed code — surfacing "Wrong email or
        // password." (the password-flow mapping) would be nonsense on a social sign-in.
        Assert.Equal(AuthErrorCode.Unknown, ex.Code);
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeAuthorizationCode_Throws_WhenNotConfigured()
    {
        var (client, _) = CreateSut(new Auth0Options("", "", ""));

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.ExchangeAuthorizationCodeAsync("c", "v"));
        Assert.Equal(AuthErrorCode.NotConfigured, ex.Code);
    }
}
