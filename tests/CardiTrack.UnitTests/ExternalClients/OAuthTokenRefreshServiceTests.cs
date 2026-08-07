using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Infrastructure.Settings;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class OAuthTokenRefreshServiceTests
{
    private readonly IDeviceConnectionRepository _deviceConnections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

    private readonly DeviceProviderSettings _config = new()
    {
        Provider = "Fitbit",
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

    [Fact]
    public async Task RefreshIfExpiredAsync_SetsTokenExpiredStatus_WhenProviderReturns400()
    {
        _encryption.Decrypt("enc_refresh").Returns("plain_refresh");
        _encryption.Decrypt("enc_access").Returns("plain_access");

        var connection = ActiveConnection(expiry: DateTime.UtcNow.AddMinutes(-10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut(new FakeHttpHandler(statusCode: HttpStatusCode.BadRequest))
                .RefreshIfExpiredAsync(connection, _config));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(connection.Id, ConnectionStatus.TokenExpired);
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
