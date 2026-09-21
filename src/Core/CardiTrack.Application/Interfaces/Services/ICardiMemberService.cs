using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface ICardiMemberService
{
    /// <summary>
    /// Creates a CardiMember and the caregiver's link to it, as one transaction.
    /// </summary>
    /// <param name="idempotencyKey">
    /// The client's own name for this attempt. When supplied, an attempt already made under this
    /// key by this caregiver returns the member it produced instead of creating a second one — the
    /// case that matters is a commit the database accepted whose acknowledgement never arrived,
    /// where the caregiver is looking at a failure and a member already exists. Null keeps the
    /// pre-existing behaviour, so a client that has not been taught to send one is unaffected.
    /// </param>
    Task<CardiMemberResponse> CreateCardiMemberAsync(
        Guid organizationId,
        Guid userId,
        CreateCardiMemberRequest request,
        string? idempotencyKey = null);

    Task<CardiMemberResponse?> GetByIdAsync(Guid id);
    Task<List<CardiMemberResponse>> GetByOrganizationIdAsync(Guid organizationId);

    /// <summary>
    /// The full M1-13 detail payload. Requires view access; throws
    /// <see cref="KeyNotFoundException"/> otherwise, so callers surface a 404.
    /// </summary>
    /// <param name="seriesEndsOn">
    /// The last day the metric series run to, in the member's own local dates; null, the usual
    /// case, ends them today. A journal entry reads its charts against the period it accounts
    /// for, which for a Monthbook is a whole series ending a month or more ago — a series that
    /// always ran to today could never reach it. Only the series moves: each metric's latest
    /// reading, status and comparison, and everything else on the payload, stay about now. A
    /// date today or in the future is today.
    /// </param>
    Task<CardiMemberDetailResponse> GetDetailAsync(
        Guid requestingUserId, Guid cardiMemberId, DateOnly? seriesEndsOn = null, CancellationToken ct = default);

    /// <summary>Saves the M1-14 edit form. Requires manage access.</summary>
    Task<CardiMemberDetailResponse> UpdateAsync(
        Guid requestingUserId, Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the member along with their caregiver links and device connections.
    /// Requires manage access. Health history is left in place for the retention window.
    /// </summary>
    Task RemoveAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Records that the medical notes were read and found still current, without changing them.
    /// Requires manage access. Rejects a member with no notes on file.
    /// </summary>
    Task<CardiMemberDetailResponse> ConfirmMedicalNotesAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Pauses monitoring for a bounded window. Requires manage access.</summary>
    Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default);

    /// <summary>Resumes monitoring ahead of the scheduled time. Requires manage access.</summary>
    Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);
}
