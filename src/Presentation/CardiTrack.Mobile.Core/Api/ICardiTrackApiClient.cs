using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Api;

public interface ICardiTrackApiClient
{
    Task<OnboardingStatusResponse> GetOnboardingStatusAsync(CancellationToken ct = default);
    Task<OrganizationResponse> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct = default);
    Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<CardiMemberResponse> CreateCardiMemberAsync(CreateCardiMemberRequest request, CancellationToken ct = default);
    Task<List<CardiMemberResponse>> GetCardiMembersAsync(CancellationToken ct = default);
    Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default);
    Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default);
}
