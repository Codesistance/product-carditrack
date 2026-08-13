using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class OAuthCodeExchangeServiceTests
{
    private readonly DeviceProviderSettings _config = new()
    {
        Provider = "GoogleHealth",
        ClientId = "client_id",
        ClientSecret = "client_secret",
        TokenUrl = "https://api.fitbit.com/oauth2/token",
        TokenLifetimeHours = 8
    };

    private static OAuthCodeExchangeService CreateSut(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        return new OAuthCodeExchangeService(factory);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ParsesTokenResponse()
    {
        var body = JsonSerializer.Serialize(new
        {
            access_token = "access",
            refresh_token = "refresh",
            expires_in = 28800,
            scope = "activity heartrate sleep",
            user_id = "FITBIT123"
        });

        var result = await CreateSut(new FakeHttpHandler(body))
            .ExchangeCodeAsync(_config, "auth_code", "carditrack://oauth/callback", "verifier");

        Assert.Equal("access", result.AccessToken);
        Assert.Equal("refresh", result.RefreshToken);
        Assert.Equal(28800, result.ExpiresInSeconds);
        Assert.Equal("activity heartrate sleep", result.Scope);
        Assert.Equal("FITBIT123", result.ProviderUserId);
    }

    [Fact]
    public async Task ExchangeCodeAsync_SendsAuthorizationCodeGrant_WithPkceVerifierAndBasicAuth()
    {
        var handler = new CapturingHttpHandler(JsonSerializer.Serialize(new { access_token = "a" }));

        await CreateSut(handler)
            .ExchangeCodeAsync(_config, "auth_code", "carditrack://oauth/callback", "verifier_xyz");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Basic", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("client_id:client_secret")),
            handler.LastRequest.Headers.Authorization?.Parameter);

        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Contains("code=auth_code", handler.LastBody);
        Assert.Contains("code_verifier=verifier_xyz", handler.LastBody);
        Assert.Contains("redirect_uri=carditrack%3A%2F%2Foauth%2Fcallback", handler.LastBody);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_WhenProviderRejectsCode()
    {
        await Assert.ThrowsAsync<OAuthExchangeException>(() =>
            CreateSut(new FakeHttpHandler(statusCode: HttpStatusCode.BadRequest))
                .ExchangeCodeAsync(_config, "bad_code", "carditrack://oauth/callback", "verifier"));
    }

    [Fact]
    public async Task ExchangeCodeAsync_Throws_WhenAccessTokenMissing()
    {
        await Assert.ThrowsAsync<OAuthExchangeException>(() =>
            CreateSut(new FakeHttpHandler("{}"))
                .ExchangeCodeAsync(_config, "auth_code", "carditrack://oauth/callback", "verifier"));
    }

    [Fact]
    public async Task ExchangeCodeAsync_DefaultsExpiry_FromProviderConfig()
    {
        var body = JsonSerializer.Serialize(new { access_token = "access" });

        var result = await CreateSut(new FakeHttpHandler(body))
            .ExchangeCodeAsync(_config, "auth_code", "carditrack://oauth/callback", "verifier");

        Assert.Equal(_config.TokenLifetimeHours * 3600, result.ExpiresInSeconds);
        Assert.Null(result.RefreshToken);
    }
}

/// <summary>Fake handler that records the last request and its form body.</summary>
internal class CapturingHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string LastBody { get; private set; } = string.Empty;

    public CapturingHttpHandler(string responseBody)
    {
        _responseBody = responseBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
    }
}
