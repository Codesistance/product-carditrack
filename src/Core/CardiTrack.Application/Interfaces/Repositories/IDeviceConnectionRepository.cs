using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceConnectionRepository : IRepository<DeviceConnection>
{
    Task<IEnumerable<DeviceConnection>> GetActiveByCardiMemberIdAsync(Guid cardiMemberId);

    /// <summary>
    /// True if any of the given CardiMembers still has a device paired — any live connection
    /// that has not been disconnected, whatever state its last sync or token left it in.
    /// Answers the onboarding-status existence check in one round trip — that endpoint runs on
    /// every app launch, so it must not fan out to a query per member.
    /// </summary>
    Task<bool> AnyActiveForCardiMembersAsync(IEnumerable<Guid> cardiMemberIds);

    Task<IEnumerable<DeviceConnection>> GetByCardiMemberIdAsync(Guid cardiMemberId);
    /// <summary>
    /// Connections due a sync, judged against each connection's own SyncFrequencyMinutes.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync();

    /// <summary>
    /// A random sample of connections eligible for syncing, for the audit pull. Carries the same
    /// active-and-not-paused filter as <see cref="GetDueForSyncAsync"/> — a paused member's data
    /// must not be collected by any path, and an audit is still collection.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetRandomSyncableSampleAsync(int count);
    Task UpdateTokenAsync(Guid id, string encryptedAccessToken, string encryptedRefreshToken, DateTime tokenExpiry);
    Task UpdateStatusAsync(Guid id, ConnectionStatus status);

    /// <summary>
    /// Records a completed pull: stamps the sync date and returns the connection to
    /// <see cref="ConnectionStatus.Connected"/>.
    /// </summary>
    /// <remarks>
    /// The status reset is the point, not a side effect. A pull that fetched a whole window is
    /// proof the connection works, and without writing that back a connection parked in
    /// <see cref="ConnectionStatus.SyncError"/> would keep reporting a fault it had already
    /// recovered from. A connection disconnected while the pull was in flight is left untouched.
    /// </remarks>
    Task MarkSyncSucceededAsync(Guid id, DateTime syncDate);

    /// <summary>
    /// Advances the history-backfill frontier — the earliest day whose data has been fetched, or
    /// confirmed absent, for this connection. Written per backfilled day so an interrupted chunk
    /// resumes where it stopped instead of refetching. Leaves a connection disconnected mid-pull
    /// untouched, for the same reason as <see cref="MarkSyncSucceededAsync"/>.
    /// </summary>
    Task UpdateHistoryBackfilledToAsync(Guid id, DateOnly backfilledTo);

    /// <summary>Records the provider's public health-user id, captured during sync.</summary>
    Task UpdateHealthUserIdAsync(Guid id, string healthUserId);

    /// <summary>
    /// Records the wearable's last-known battery reading, captured during sync. Last value wins —
    /// battery is volatile telemetry with no history behind it, so each write overwrites rather
    /// than appends. Leaves a connection disconnected mid-pull untouched, for the same reason as
    /// <see cref="MarkSyncSucceededAsync"/>.
    /// </summary>
    /// <param name="level">Percentage 0–100, or null when the provider reported only a band.</param>
    /// <param name="status">The provider's band — High, Medium, Low or Empty.</param>
    /// <param name="readAtUtc">When the reading was captured, so staleness can be judged later.</param>
    Task UpdateBatteryAsync(Guid id, int? level, string? status, DateTime readAtUtc);

    /// <summary>
    /// The syncable connections a webhook notification for this health-user id addresses —
    /// same active-and-not-paused semantics as <see cref="GetDueForSyncAsync"/>: a notification
    /// must never resurrect collection for a paused or removed member.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetSyncableByHealthUserIdAsync(string healthUserId);
}
