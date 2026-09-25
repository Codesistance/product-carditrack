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
    /// Records whether the wearer has completed Irregular Rhythm Notifications setup on their own
    /// device and is currently enrolled in having their data screened for AFib. Both null where
    /// the profile could not be read — an ungranted scope included.
    /// </summary>
    /// <param name="readAtUtc">When the profile was read, so staleness can be judged later.</param>
    Task UpdateIrnProfileAsync(Guid id, bool? onboarded, bool? enrolled, DateTime readAtUtc);

    /// <summary>
    /// Connections the provider has refused, due another attempt at their refresh token — the
    /// auth-recovery probe's input set.
    /// </summary>
    /// <remarks>
    /// Deliberately the mirror image of <see cref="GetDueForSyncAsync"/>: it returns exactly the
    /// statuses that one excludes, because a connection out of the sync rotation is a connection
    /// nothing else will ever touch again. Paused and removed members stay out — recovering a
    /// connection is not collection, but it is the first step of it, and a paused member's device
    /// should not quietly come back into service. A connection with no refresh token stays out
    /// too: there is nothing to retry with, and only re-consent will produce one.
    /// </remarks>
    Task<IEnumerable<DeviceConnection>> GetDueForAuthRecoveryAsync(DateTime utcNow);

    /// <summary>
    /// Records a failed recovery attempt: increments the counter and schedules the next try.
    /// </summary>
    Task MarkAuthRecoveryFailedAsync(Guid id, DateTime nextAttemptAt);

    /// <summary>
    /// Returns a recovered connection to service — <see cref="ConnectionStatus.Connected"/>, the
    /// backoff cleared — so the ordinary sync rotation picks it up on its next pass.
    /// </summary>
    Task MarkAuthRecoveredAsync(Guid id);

    /// <summary>
    /// The syncable connections a webhook notification for this health-user id addresses —
    /// same active-and-not-paused semantics as <see cref="GetDueForSyncAsync"/>: a notification
    /// must never resurrect collection for a paused or removed member.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetSyncableByHealthUserIdAsync(string healthUserId);

    /// <summary>
    /// Whether any live connection other than <paramref name="excludingId"/> — on any member, and
    /// suspended ones included, since they keep their tokens — reads through this provider account.
    /// Such a connection shares the provider grant, so revoking the grant would cut it off too.
    /// </summary>
    Task<bool> AnyOtherActiveWithHealthUserIdAsync(Guid excludingId, string healthUserId);

    /// <summary>
    /// Serializes changes to one member's set of devices until the current transaction ends, and
    /// reports whether the member still exists and is active, read under the lock. Must be called
    /// inside a transaction, before the member's connections are read.
    /// </summary>
    /// <remarks>
    /// The rules over a member's devices span rows, not one row: one primary, one connection per
    /// provider account, never the last collecting device suspended. Each is a read of the whole
    /// set followed by a write. Two such changes interleaving can each pass their check and
    /// together break the rule — two replacements each promoting their new device, two
    /// suspensions each seeing the other device still collecting. Holding this across read and
    /// write makes the second change read what the first committed.
    /// <para>
    /// Member erasure takes the same lock, so the answer is what a change must act on: a change
    /// that waited behind an erasure finds the member gone, and must not recreate a connection or
    /// a queued revocation for someone whose every row has just been deleted.
    /// </para>
    /// </remarks>
    Task<bool> LockMemberDevicesAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Whether the connection is suspended right now, read from the database rather than from an
    /// entity loaded earlier. The collection queries leave suspended connections out, but a batch
    /// selected before a suspension committed still holds them; this is the check each pull makes
    /// when it actually runs.
    /// </summary>
    Task<bool> IsSuspendedAsync(Guid id);

}
