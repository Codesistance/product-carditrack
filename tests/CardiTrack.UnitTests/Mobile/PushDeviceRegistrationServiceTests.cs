using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
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

    private Task<PushDeviceTokenResponse> RegisterAsync(PushDeviceRegistrationService sut) =>
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

        var sut = new PushDeviceRegistrationService(_api);

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

        var sut = new PushDeviceRegistrationService(_api);

        await Assert.ThrowsAsync<HttpRequestException>(() => RegisterAsync(sut));
        await sut.UnregisterAsync("install-a").WaitAsync(TimeSpan.FromSeconds(5));

        await _api.Received(1).UnregisterPushDeviceAsync("install-a", Arg.Any<CancellationToken>());
    }
}
