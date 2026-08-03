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

    Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default);
}
