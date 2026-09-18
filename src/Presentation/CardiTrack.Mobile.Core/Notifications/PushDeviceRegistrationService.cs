using System.Security.Cryptography;
using System.Text;
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
    /// The session whose registration was last given up, once <see cref="UnregisterAsync"/> has
    /// run — guarded by <see cref="_gate"/>, so it is read and written only inside it.
    /// </summary>
    /// <remarks>
    /// Ordering alone is not enough. A registration can pass every check it makes before the
    /// gate, queue behind the sign-out's DELETE, and post the moment the gate frees — still
    /// inside the session, because <c>SignOutAsync</c> clears the token store only after the
    /// release returns. It would put the departing caregiver's row back for the next person
    /// holding the phone. Recording which session was released closes that: a registration
    /// carrying it has been overtaken and is dropped, and the next sign-in brings a different
    /// session, so nothing has to re-arm anything.
    ///
    /// The access token stands in for the session's identity and is hashed rather than kept: this
    /// only ever has to answer "the same one?", and a credential held a second time in a second
    /// field is a credential in one more place than it needs to be.
    /// </remarks>
    private string? _releasedSession;

    public PushDeviceRegistrationService(ICardiTrackApiClient api, ITokenStore tokens)
    {
        _api = api;
        _tokens = tokens;
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
            var session = await SessionAsync();
            if (session is null || session == _releasedSession)
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
            _releasedSession = await SessionAsync() ?? _releasedSession;

            await _api.UnregisterPushDeviceAsync(deviceId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A stable, non-reversible mark for the signed-in session, or null when there is none. Only
    /// ever compared with another of its own — see <see cref="_releasedSession"/>.
    /// </summary>
    private async Task<string?> SessionAsync()
    {
        var tokens = await _tokens.GetAsync();
        return tokens is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokens.AccessToken)));
    }

    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        _api.AckDeliveredAsync(deliveryId, ackToken, ct);
}
