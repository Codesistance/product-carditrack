using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface ICardiMemberService
{
    Task<CardiMemberResponse> CreateCardiMemberAsync(
        Guid organizationId,
        Guid userId,
        CreateCardiMemberRequest request);

    Task<CardiMemberResponse?> GetByIdAsync(Guid id);
    Task<List<CardiMemberResponse>> GetByOrganizationIdAsync(Guid organizationId);

    /// <summary>
    /// The full M1-13 detail payload. Requires view access; throws
    /// <see cref="KeyNotFoundException"/> otherwise, so callers surface a 404.
    /// </summary>
    Task<CardiMemberDetailResponse> GetDetailAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Saves the M1-14 edit form. Requires manage access.</summary>
    Task<CardiMemberDetailResponse> UpdateAsync(
        Guid requestingUserId, Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the member along with their caregiver links and device connections.
    /// Requires manage access. Health history is left in place for the retention window.
    /// </summary>
    Task RemoveAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Pauses monitoring for a bounded window. Requires manage access.</summary>
    Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default);

    /// <summary>Resumes monitoring ahead of the scheduled time. Requires manage access.</summary>
    Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);
}
