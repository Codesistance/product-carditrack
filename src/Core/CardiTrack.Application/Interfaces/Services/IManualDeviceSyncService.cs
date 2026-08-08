using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// A caregiver-triggered pull of one CardiMember's wearable data — what the dashboard's
/// refresh button does (issue #67).
/// </summary>
/// <remarks>
/// This is deliberately not a background job and does no polling: it runs inside the request
/// that asked for it, so the scheduled pull remains <c>CardiTrack.Worker</c>'s alone per
/// CLAUDE.md. Each connection still goes through the same <see cref="IDeviceSyncService"/> that
/// <c>WearableSyncWorker</c> drives, so a manual sync and a scheduled one can never diverge in
/// what they store.
/// </remarks>
public interface IManualDeviceSyncService
{
    /// <summary>
    /// Pulls every active connection the member has, and reports what happened per device.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// The member doesn't exist, or the user may not view them.
    /// </exception>
    /// <exception cref="ManualSyncUnavailableException">
    /// Monitoring is paused, the member has no connected device, or a sync ran too recently.
    /// </exception>
    Task<DeviceSyncResultResponse> SyncMemberAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);
}
