using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Notifications;
using NSubstitute;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Registration is fire-and-forget (the shell on launch, the first device connection) and
/// unregistration runs at sign-out, so the two can overlap. If a registration still in flight
/// reaches the server *after* the sign-out's unregister, the departing caregiver's row is put
/// back and they stay reachable on a handset they have just handed over — the exact leak the
/// unregister call was added to close.
/// </summary>
public class PushDeviceRegistrationServiceTests
{
    private readonly ICardiTrackApiClient _api = Substitute.For<ICardiTrackApiClient>();
    private readonly ITokenStore _tokens = Substitute.For<ITokenStore>();

    private static AuthTokens Session(string accessToken) =>
        new(accessToken, RefreshToken: null, IdToken: null, DateTimeOffset.UtcNow.AddHours(1));

    public PushDeviceRegistrationServiceTests() =>
        _tokens.GetAsync().Returns(Session("the-signed-in-session"));

    private readonly SessionGeneration _session = new();

    private PushDeviceRegistrationService CreateSut() => new(_api, _tokens, _session);

    private Task<PushDeviceTokenResponse?> RegisterAsync(PushDeviceRegistrationService sut) =>
        sut.RegisterAsync(
            "install-a", DevicePlatform.Android, appVersion: "1.0+1", token: "fcm-token",
            OsAuthorizationStatus.Granted, safetyChannelEnabled: true);

    [Fact]
    public async Task AnUnregisterWaitsForARegistrationAlreadyInFlight()
    {
        var registrationReached = new TaskCompletionSource();
        var releaseRegistration = new TaskCompletionSource();
        var order = new List<string>();

        _api.RegisterPushDeviceAsync(Arg.Any<RegisterPushDeviceRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                registrationReached.SetResult();
                await releaseRegistration.Task;
                order.Add("register");
                return new PushDeviceTokenResponse();
            });
        _api.UnregisterPushDeviceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("unregister");
                return Task.CompletedTask;
            });

        var sut = CreateSut();

        var registering = RegisterAsync(sut);
        await registrationReached.Task;

        var unregistering = sut.UnregisterAsync("install-a");

        // The unregister must not have reached the API yet — the registration still holds the gate.
        Assert.Empty(order);

        releaseRegistration.SetResult();
        await Task.WhenAll(registering, unregistering);

        Assert.Equal(["register", "unregister"], order);
    }

    [Fact]
    public async Task AFailedRegistrationStillReleasesTheGate()
    {
        // Registration failures are swallowed by the coordinator and are entirely ordinary —
        // offline, a denied permission, a 500. A gate left held by one would hang the next
        // sign-out instead.
        _api.RegisterPushDeviceAsync(Arg.Any<RegisterPushDeviceRequest>(), Arg.Any<CancellationToken>())
            .Returns<PushDeviceTokenResponse>(_ => throw new HttpRequestException("offline"));

        var sut = CreateSut();

        await Assert.ThrowsAsync<HttpRequestException>(() => RegisterAsync(sut));
        await sut.UnregisterAsync("install-a").WaitAsync(TimeSpan.FromSeconds(5));

        await _api.Received(1).UnregisterPushDeviceAsync("install-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARegistrationBelongingToAReleasedSessionIsDropped()
    {
        // Ordering alone does not settle this one. A registration can be waiting on the gate
        // while the sign-out's DELETE goes out, and still be inside the session when it acquires
        // it — SignOutAsync clears the token store only after the release returns. Posting then
        // would put the departing caregiver's row back for the next person holding the phone.
        var sut = CreateSut();

        await sut.UnregisterAsync("install-a");
        var result = await RegisterAsync(sut);

        Assert.Null(result);
        await _api.DidNotReceiveWithAnyArgs().RegisterPushDeviceAsync(default!, default);
    }

    [Fact]
    public async Task ARegistrationSurvivingATokenRefreshIsStillDropped()
    {
        // The marker is the session's generation, not anything token-shaped. A refresh replaces
        // the access token without ending the session, and a registration that read the new one
        // would otherwise look like a fresh sign-in and post after the release.
        var sut = CreateSut();

        await sut.UnregisterAsync("install-a");
        _tokens.GetAsync().Returns(Session("refreshed-during-the-unregister"));

        Assert.Null(await RegisterAsync(sut));
        await _api.DidNotReceiveWithAnyArgs().RegisterPushDeviceAsync(default!, default);
    }

    [Fact]
    public async Task ARegistrationForTheNextSessionIsSentNormally()
    {
        // Nothing has to re-arm anything: signing in advances the generation, so the record of
        // the released session simply stops matching.
        _api.RegisterPushDeviceAsync(Arg.Any<RegisterPushDeviceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PushDeviceTokenResponse());

        var sut = CreateSut();
        await sut.UnregisterAsync("install-a");

        _session.Advance();
        _tokens.GetAsync().Returns(Session("the-next-caregivers-session"));
        var result = await RegisterAsync(sut);

        Assert.NotNull(result);
        await _api.Received(1).RegisterPushDeviceAsync(
            Arg.Any<RegisterPushDeviceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARegistrationWithNoSessionLeftIsDropped()
    {
        // The permission prompt and token fetch can outlast the session that asked for them.
        _tokens.GetAsync().Returns((AuthTokens?)null);

        Assert.Null(await RegisterAsync(CreateSut()));
        await _api.DidNotReceiveWithAnyArgs().RegisterPushDeviceAsync(default!, default);
    }
}
