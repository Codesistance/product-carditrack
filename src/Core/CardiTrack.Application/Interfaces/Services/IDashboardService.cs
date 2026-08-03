using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IDashboardService
{
    /// <summary>
    /// Composes the mobile dashboard payload for one CardiMember. Throws
    /// KeyNotFoundException when the member doesn't exist or the requesting user
    /// has no active health-data-visible link to them.
    /// </summary>
    Task<DashboardResponse> GetDashboardAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);
}
