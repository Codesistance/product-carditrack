using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Wearable connection lifecycle for the M1-05..M1-07 wizard: list a CardiMember's devices,
/// initiate a PKCE server-OAuth flow, and complete it by exchanging the authorization code.
/// </summary>
public interface IDeviceConnectionService
{
    Task<DeviceListResponse> GetDevicesAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    Task<OAuthInitiationResponse> InitiateConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves the app deep link cached at initiation for a pending state token, without
    /// consuming it. Used by the anonymous oauth/redirect bounce endpoint that forwards the
    /// provider's https redirect back into the mobile app. Returns null for unknown providers
    /// or unknown/expired state.
    ///
    /// What comes back is safe for the caller to append callback parameters to: it is always
    /// an absolute app-scheme URI with no fragment. Anything else resolves to null.
    /// </summary>
    Task<string?> GetAppRedirectUriAsync(string provider, string state, CancellationToken ct = default);

    Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default);

    /// <summary>
    /// Disconnects a device (M1-15 "Remove Device"): soft-deletes the connection and discards
    /// its stored OAuth tokens, so revoking access at the provider is the user's only remaining
    /// step. Promotes another connection to primary if this one was it.
    /// </summary>
    Task DisconnectAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>Makes this the CardiMember's primary device, demoting the previous one (M1-15).</summary>
    Task<DeviceResponse> SetPrimaryAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// M1-15 "Refresh Connection": renews the OAuth token if it has expired and reports the
    /// connection's current state. Deliberately does <em>not</em> pull health data — that is the
    /// sync worker's job, per the background-job rule in CLAUDE.md.
    /// </summary>
    Task<DeviceResponse> RefreshConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);
}
