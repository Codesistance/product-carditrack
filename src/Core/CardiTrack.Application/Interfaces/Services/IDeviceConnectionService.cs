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
    /// </summary>
    Task<string?> GetAppRedirectUriAsync(string provider, string state, CancellationToken ct = default);

    Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default);
}
