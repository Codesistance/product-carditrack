using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Records a caregiver's request to re-read a connection's history (M1-15 "Re-pull History").
/// Request-scoped: it writes the work order and returns; <c>HistoryRepullWorker</c> in
/// <c>CardiTrack.Worker</c> is the only thing that executes it, per CLAUDE.md.
/// </summary>
public interface IDeviceHistoryRepullService
{
    /// <summary>
    /// Queues a re-pull of the last <paramref name="days"/> complete days for one connection.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The caller cannot see the member, or the device is not theirs.</exception>
    /// <exception cref="Application.Exceptions.HistoryRepullUnavailableException">
    /// Monitoring is paused, the connection cannot sync, a re-pull is already open, or one
    /// finished too recently.
    /// </exception>
    Task<DeviceHistoryRepullResponse> RequestAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, int days, CancellationToken ct = default);
}
