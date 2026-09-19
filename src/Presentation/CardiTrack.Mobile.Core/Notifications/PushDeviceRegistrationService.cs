using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Core.Notifications;

public interface IPushDeviceRegistrationService
{
    /// <returns>
    /// The stored registration, or null when the call was dropped because the session it was
    /// made for has gone — see <see cref="PushDeviceRegistrationService"/>.
    /// </returns>
    Task<PushDeviceTokenResponse?> RegisterAsync(
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
    private readonly ITokenStore _tokens;
    private readonly SessionGeneration? _session;

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

    /// <summary>
    /// The <see cref="SessionGeneration"/> that last gave up its registration, or null before any
    /// has — guarded by <see cref="_gate"/>, so it is read and written only inside it.
    /// </summary>
    /// <remarks>
    /// Ordering alone is not enough. A registration can pass every check it makes before the
    /// gate, queue behind the sign-out's DELETE, and post the moment the gate frees — still
    /// inside the session, because <c>SignOutAsync</c> clears the token store only after the
    /// release returns. It would put the departing caregiver's row back for the next person
    /// holding the phone.
    ///
    /// The generation is the identity to compare, not anything token-shaped: it moves on sign-in
    /// and sign-out and on nothing else, where an access token is replaced by any refresh in
    /// between — and a registration whose token was refreshed while the release was in flight
    /// would then read as a new session and post anyway. It is the same mechanism
    /// <c>CardiTrackApiClient</c> already uses to decide whether a response has outlived the
    /// session that asked for it. The next sign-in advances it, so nothing has to re-arm.
    /// </remarks>
    private int? _releasedGeneration;

    public PushDeviceRegistrationService(
        ICardiTrackApiClient api, ITokenStore tokens, SessionGeneration? session = null)
    {
        _api = api;
        _tokens = tokens;
        _session = session;
    }

    public async Task<PushDeviceTokenResponse?> RegisterAsync(
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
            // Inside the gate, not before it: a check made outside proves only that the session
            // was alive when this call queued, which is exactly the registration that overtakes a
            // sign-out.
            if (await _tokens.GetAsync() is null || CurrentGeneration() == _releasedGeneration)
                return null;

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
            // Recorded before the call, not after it: a DELETE that throws still means this
            // session has given up its registration and must not quietly take it back. The
            // caller decides what to do about the failure; this only decides that no
            // registration of this session's follows it.
            _releasedGeneration = CurrentGeneration();

            await _api.UnregisterPushDeviceAsync(deviceId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The current session's generation, defaulting to zero where none is wired — the same
    /// <c>_session?.Current ?? 0</c> reading <c>CardiTrackApiClient</c> takes.
    /// </summary>
    private int CurrentGeneration() => _session?.Current ?? 0;

    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        _api.AckDeliveredAsync(deliveryId, ackToken, ct);
}
