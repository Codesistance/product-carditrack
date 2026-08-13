using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class OAuthTokenRefreshServiceTests
{
    private readonly IDeviceConnectionRepository _deviceConnections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

    private readonly DeviceProviderSettings _config = new()
    {
        Provider = "GoogleHealth",
        ClientId = "client_id",
        ClientSecret = "client_secret",
        TokenUrl = "https://api.fitbit.com/oauth2/token",
        TokenLifetimeHours = 8
    };

    private OAuthTokenRefreshService CreateSut(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        return new OAuthTokenRefreshService(_deviceConnections, _encryption, factory);
    }

    private DeviceConnection ActiveConnection(DateTime? expiry = null) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = DeviceType.Fitbit,
        AccessToken = "enc_access",
        RefreshToken = "enc_refresh",
        TokenExpiry = expiry ?? DateTime.UtcNow.AddHours(2),
        ConnectionStatus = ConnectionStatus.Connected
    };

    [Fact]
    public async Task RefreshIfExpiredAsync_NoOp_WhenTokenNotExpired()
    {
        _encryption.Decrypt("enc_access").Returns("plain_access");
        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddHours(2));

        var result = await CreateSut(new FakeHttpHandler()).RefreshIfExpiredAsync(connection, _config);

        Assert.Equal("plain_access", result);
        await _deviceConnections.DidNotReceive()
            .UpdateTokenAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task RefreshIfExpiredAsync_DecryptsRefreshToken_BeforePosting()
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");
        _encryption.Encrypt(Arg.Any<string>()).Returns("enc_new");

        var tokenResponse = BuildTokenResponse("new_access", "new_refresh", 28800);
        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await CreateSut(new FakeHttpHandler(tokenResponse)).RefreshIfExpiredAsync(connection, _config);

        _encryption.Received(1).Decrypt("enc_refresh");
    }

    [Fact]
    public async Task RefreshIfExpiredAsync_EncryptsNewTokens_BeforePersisting()
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");
        _encryption.Encrypt("new_access").Returns("enc_new_access");
        _encryption.Encrypt("new_refresh").Returns("enc_new_refresh");

        var tokenResponse = BuildTokenResponse("new_access", "new_refresh", 28800);
        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await CreateSut(new FakeHttpHandler(tokenResponse)).RefreshIfExpiredAsync(connection, _config);

        await _deviceConnections.Received(1)
            .UpdateTokenAsync(connection.Id, "enc_new_access", "enc_new_refresh", Arg.Any<DateTime>());
    }

    [Fact]
    public async Task RefreshIfExpiredAsync_UpdatesDbWithNewExpiry_OnSuccess()
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");
        _encryption.Encrypt(Arg.Any<string>()).Returns("enc_new");

        var tokenResponse = BuildTokenResponse("new_access", "new_refresh", 28800);
        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));
        var before = DateTime.UtcNow;

        await CreateSut(new FakeHttpHandler(tokenResponse)).RefreshIfExpiredAsync(connection, _config);

        await _deviceConnections.Received(1)
            .UpdateTokenAsync(connection.Id, Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<DateTime>(d => d > before));
    }

    // A 4xx is the provider refusing the grant itself — revoked, already spent, wrong client.
    // Nothing but re-consent fixes that, so the connection is retired from syncing and the app
    // is told to ask for a reconnect.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefreshIfExpiredAsync_SetsTokenExpiredStatus_WhenProviderRefusesTheGrant(
        HttpStatusCode status)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(statusCode: status))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(connection.Id, ConnectionStatus.TokenExpired);
    }

    // These say nothing about the grant — the provider is having a bad minute, or throttling us.
    // Marking them TokenExpired retired a working connection on a transient fault: it dropped out
    // of the sync rotation for good and the app started asking the user to reconnect a device
    // that had never lost authorisation.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task RefreshIfExpiredAsync_LeavesStatusAlone_WhenTheProviderFailsTransiently(
        HttpStatusCode status)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(statusCode: status))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    // Our URL, method or content type is wrong — a deployment fault, not a revoked grant. Every
    // connection for the provider hits this at once, so misreading it retires the whole fleet.
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task RefreshIfExpiredAsync_LeavesStatusAlone_WhenTheRequestItselfWasWrong(
        HttpStatusCode status)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(statusCode: status))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    // A 400 carries both "your refresh token is dead" and "your client credentials are wrong",
    // and only the error code separates them. A badly rotated secret must not retire the fleet.
    [Theory]
    [InlineData("invalid_client")]
    [InlineData("invalid_request")]
    [InlineData("unsupported_grant_type")]
    [InlineData("invalid_scope")]
    [InlineData("unauthorized_client")]
    public async Task RefreshIfExpiredAsync_LeavesStatusAlone_WhenTheFaultIsOurRequestNotTheGrant(
        string errorCode)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(BuildErrorResponse(errorCode), HttpStatusCode.BadRequest))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("invalid_token")]
    [InlineData("expired_token")]
    [InlineData("access_denied")]
    public async Task RefreshIfExpiredAsync_SetsTokenExpiredStatus_WhenTheErrorCodeMeansTheGrantIsDead(
        string errorCode)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(BuildErrorResponse(errorCode), HttpStatusCode.BadRequest))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(connection.Id, ConnectionStatus.TokenExpired);
    }

    // Not every provider answers in the spec's shape. With no code to read, the status is all we
    // have, and a 401 on a refresh is far more often a dead grant than anything else.
    [Theory]
    [InlineData("<html>Bad Request</html>")]
    [InlineData("{}")]
    public async Task RefreshIfExpiredAsync_FallsBackToTheStatus_WhenTheBodyCarriesNoErrorCode(
        string body)
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(body, HttpStatusCode.Unauthorized))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(connection.Id, ConnectionStatus.TokenExpired);
    }

    // The request never reached the provider, so it never said anything about the grant at all.
    [Fact]
    public async Task RefreshIfExpiredAsync_LeavesStatusAlone_WhenTheCallNeverReachedTheProvider()
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new ThrowingHttpHandler(new HttpRequestException("no such host")))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    [Fact]
    public async Task RefreshIfExpiredAsync_Throws_WhenProviderNotConfigured()
    {
        // Connection has no RefreshToken
        var connection = new DeviceConnection
        {
            Id = Guid.NewGuid(),
            DeviceType = DeviceType.Fitbit,
            AccessToken = null,
            RefreshToken = null,
            TokenExpiry = DateTime.UtcNow.AddMinutes(-10)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler()).RefreshIfExpiredAsync(connection, _config));
    }

    private static string BuildErrorResponse(string errorCode)
        => JsonSerializer.Serialize(new { error = errorCode });

    private static string BuildTokenResponse(string accessToken, string refreshToken, int expiresIn)
        => JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = expiresIn
        });
}

/// <summary>Simple fake HTTP handler for unit tests.</summary>
internal class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;

    public FakeHttpHandler(string responseBody = "{}", HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        });
}

/// <summary>A handler that fails before any response exists — DNS, connect and timeout faults.</summary>
internal class ThrowingHttpHandler(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromException<HttpResponseMessage>(failure);
}
