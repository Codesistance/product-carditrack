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
}
