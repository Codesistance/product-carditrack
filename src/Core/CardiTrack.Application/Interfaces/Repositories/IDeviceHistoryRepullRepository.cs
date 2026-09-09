using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Caregiver-requested history re-pulls (<see cref="DeviceHistoryRepull"/>): the API records
/// them, the Worker drains them, and the device list reads the latest one per connection.
/// </summary>
public interface IDeviceHistoryRepullRepository : IRepository<DeviceHistoryRepull>
{
    /// <summary>
    /// The connection's open request — Pending or InProgress — if it has one. At most one can
    /// exist; the partial unique index on the table is what makes that true under a race.
    /// </summary>
    Task<DeviceHistoryRepull?> GetOpenByConnectionIdAsync(Guid deviceConnectionId, CancellationToken ct = default);

    /// <summary>
    /// When the connection's most recent <em>completed</em> re-pull finished, or null if none has.
    /// Failed and cancelled requests do not count: a re-pull that never delivered should be
    /// retryable at once rather than sit out the cooldown.
    /// </summary>
    Task<DateTime?> GetLastCompletedAtAsync(Guid deviceConnectionId, CancellationToken ct = default);

    /// <summary>
    /// The newest request per connection, for the connections given — one query for the whole
    /// device list rather than one per card.
    /// </summary>
    Task<IReadOnlyList<DeviceHistoryRepull>> GetLatestByConnectionIdsAsync(
        IEnumerable<Guid> deviceConnectionIds, CancellationToken ct = default);

    /// <summary>
    /// Open requests for the Worker to advance, oldest request first so nobody waits behind a
    /// newer tap, capped at <paramref name="limit"/>. Tracked, so the caller can mutate and save.
    /// </summary>
    Task<IReadOnlyList<DeviceHistoryRepull>> GetDueAsync(int limit, CancellationToken ct = default);
}
