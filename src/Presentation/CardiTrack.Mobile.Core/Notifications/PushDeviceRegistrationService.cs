using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Notifications;

public interface IPushDeviceRegistrationService
{
    Task<PushDeviceTokenResponse> RegisterAsync(
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string token,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct = default);

    Task UnregisterAsync(string deviceId, CancellationToken ct = default);

    Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default);
}

/// <summary>
/// The testable half of push device registration — the platform-specific token retrieval and OS
/// permission APIs live in <c>CardiTrack.Mobile</c>; this just turns already-resolved values into
/// API calls. Kept out of <c>CardiTrack.Mobile</c> so the request-shaping and error handling here
/// are exercised by <c>CardiTrack.UnitTests</c> without a MAUI host.
/// </summary>
public class PushDeviceRegistrationService : IPushDeviceRegistrationService
{
    private readonly ICardiTrackApiClient _api;

    /// <summary>
    /// Registration is fire-and-forget from the shell and from the first device connection, and
    /// unregistration runs at sign-out. Left unserialized, a registration still in flight when a
    /// caregiver signs out lands *after* the unregister and puts the departing caregiver's row
    /// back — reachable on a handset they have just handed over. The gate makes the sign-out's
    /// call the last one of the pair to reach the server, whichever started first.
    ///
    /// One instance, one gate: this service is registered as a singleton, and the API client it
    /// wraps is the only thing either call touches.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PushDeviceRegistrationService(ICardiTrackApiClient api)
    {
        _api = api;
    }

    public async Task<PushDeviceTokenResponse> RegisterAsync(
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string token,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await _api.RegisterPushDeviceAsync(new RegisterPushDeviceRequest
            {
                DeviceId = deviceId,
                Platform = platform,
                AppVersion = appVersion,
                Token = token,
                OsAuthorizationStatus = osAuthorizationStatus,
                SafetyChannelEnabled = safetyChannelEnabled
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _api.UnregisterPushDeviceAsync(deviceId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        _api.AckDeliveredAsync(deliveryId, ackToken, ct);
}
