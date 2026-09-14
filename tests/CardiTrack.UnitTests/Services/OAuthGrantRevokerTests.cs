using System.Net;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Whether a disconnect actually ends the grant at the provider, rather than forgetting it here.
/// </summary>
/// <remarks>
/// The distinction this pins is the reason the class exists: until this call is made, CardiTrack
/// stays listed among the apps with access to the wearer's health data, and a copy of the refresh
/// token is still exchangeable for readings. Every case below is about being able to tell the
/// difference between "the grant is gone" and "we stopped asking".
/// </remarks>
public class OAuthGrantRevokerTests
{
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly FakeHandler _handler = new();

    public OAuthGrantRevokerTests() =>
        _encryption.Decrypt(Arg.Any<string>()).Returns(c => "plain-" + c.Arg<string>());

    [Fact]
    public async Task ARefreshTokenIsSentToTheProvidersRevocationEndpoint()
    {
        _handler.Respond(HttpStatusCode.OK);

        var revoked = await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh"));

        Assert.True(revoked);
        Assert.Equal("https://provider.example/revoke", _handler.LastUri!.ToString());
        Assert.Equal(HttpMethod.Post, _handler.LastMethod);
        Assert.Equal("token=plain-enc-refresh", _handler.LastBody);
    }

    /// <summary>
    /// The refresh token, not the access token, when both are present: for Google revoking the
    /// refresh token ends the whole grant, while revoking an access token ends only that hour.
    /// </summary>
    [Fact]
    public async Task TheRefreshTokenIsPreferredOverTheAccessToken()
    {
        _handler.Respond(HttpStatusCode.OK);

        await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh", accessToken: "enc-access"));

        Assert.Equal("token=plain-enc-refresh", _handler.LastBody);
    }

    /// <summary>With no refresh token there is still a grant worth ending as far as we can.</summary>
    [Fact]
    public async Task TheAccessTokenIsUsedWhenThereIsNoRefreshToken()
    {
        _handler.Respond(HttpStatusCode.OK);

        await CreateSut().TryRevokeAsync(Connection(accessToken: "enc-access"));

        Assert.Equal("token=plain-enc-access", _handler.LastBody);
    }

    /// <summary>
    /// Google answers 400 <c>invalid_token</c> for a token that is already dead, where RFC 7009
    /// §2.2 suggests 200. That is the outcome revocation wanted, so it counts as success —
    /// otherwise every reconnect-then-disconnect would log a warning about a grant already gone,
    /// and the real failures would be lost among them.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AGrantTheProviderWillNoLongerHonour_CountsAsRevoked(HttpStatusCode status)
    {
        _handler.Respond(status);

        Assert.True(await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh")));
    }

    /// <summary>
    /// A provider that is merely broken has told us nothing about the grant, so the honest answer
    /// is "not confirmed" — the caller decides what to say about it.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AProviderFailure_IsReportedAsUnconfirmed_NotThrown(HttpStatusCode status)
    {
        _handler.Respond(status);

        Assert.False(await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh")));
    }

    /// <summary>
    /// A network failure must not escape. Disconnecting a device is the caregiver's decision and
    /// it is irreversible on our side; a DNS blip cannot be allowed to fail the request they made.
    /// </summary>
    [Fact]
    public async Task ANetworkFailure_IsReportedAsUnconfirmed_NotThrown()
    {
        _handler.Throw(new HttpRequestException("no route to host"));

        Assert.False(await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh")));
    }

    [Fact]
    public async Task AConnectionWithNoTokenAtAll_IsNotSentToTheProvider()
    {
        Assert.False(await CreateSut().TryRevokeAsync(Connection()));
        Assert.Null(_handler.LastUri);
    }

    /// <summary>
    /// The supported local state: no revocation endpoint configured. It must be a quiet no-op, not
    /// a failed HTTP call to an empty URL.
    /// </summary>
    [Fact]
    public async Task ADeviceTypeWithNoConfiguredEndpoint_IsNotSentAnywhere()
    {
        Assert.False(await CreateSut(revocationUrl: "")
            .TryRevokeAsync(Connection(refreshToken: "enc-refresh")));
        Assert.Null(_handler.LastUri);
    }

    /// <summary>
    /// A token that will not decrypt is a live grant this system can no longer end by itself —
    /// worth reporting rather than crashing the disconnect that found it.
    /// </summary>
    [Fact]
    public async Task ATokenThatWillNotDecrypt_IsReportedAsUnconfirmed_NotThrown()
    {
        _encryption.Decrypt(Arg.Any<string>()).Returns(_ => throw new FormatException("not ciphertext"));

        Assert.False(await CreateSut().TryRevokeAsync(Connection(refreshToken: "enc-refresh")));
        Assert.Null(_handler.LastUri);
    }

    /// <summary>A device type no provider block claims cannot be revoked and must not throw.</summary>
    [Fact]
    public async Task ADeviceTypeNoProviderClaims_IsNotSentAnywhere()
    {
        Assert.False(await CreateSut()
            .TryRevokeAsync(Connection(refreshToken: "enc-refresh", deviceType: DeviceType.Oura)));
        Assert.Null(_handler.LastUri);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static DeviceConnection Connection(
        string? refreshToken = null,
        string? accessToken = null,
        DeviceType deviceType = DeviceType.Fitbit) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = deviceType,
        RefreshToken = refreshToken,
        AccessToken = accessToken,
    };

    private OAuthGrantRevoker CreateSut(string revocationUrl = "https://provider.example/revoke")
    {
        var providers = Options.Create(new List<DeviceProviderSettings>
        {
            new()
            {
                Provider = "GoogleHealth",
                DeviceTypes = ["Fitbit", "GooglePixelWatch"],
                RevocationUrl = revocationUrl,
            },
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(_handler, disposeHandler: false));

        return new OAuthGrantRevoker(
            providers, _encryption, factory, NullLogger<OAuthGrantRevoker>.Instance);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private Exception? _throw;

        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status) => _status = status;
        public void Throw(Exception ex) => _throw = ex;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throw is not null)
                throw _throw;

            return new HttpResponseMessage(_status) { Content = new StringContent(string.Empty) };
        }
    }
}
