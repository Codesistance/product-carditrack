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

    public PushDeviceRegistrationService(ICardiTrackApiClient api)
    {
        _api = api;
    }

    public Task<PushDeviceTokenResponse> RegisterAsync(
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string token,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct = default) =>
        _api.RegisterPushDeviceAsync(new RegisterPushDeviceRequest
        {
            DeviceId = deviceId,
            Platform = platform,
            AppVersion = appVersion,
            Token = token,
            OsAuthorizationStatus = osAuthorizationStatus,
            SafetyChannelEnabled = safetyChannelEnabled
        }, ct);

    public Task UnregisterAsync(string deviceId, CancellationToken ct = default) =>
        _api.UnregisterPushDeviceAsync(deviceId, ct);

    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        _api.AckDeliveredAsync(deliveryId, ackToken, ct);
}
