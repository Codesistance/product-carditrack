using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CardiTrack.Infrastructure.Persistence;

/// <summary>
/// The transaction-scoped advisory lock that serializes changes to one member's devices: device
/// connects, removals and suspensions, and member erasure. One definition of the key, so every
/// party that takes it takes the same lock.
/// </summary>
/// <remarks>
/// An advisory lock rather than <c>FOR UPDATE</c> on the member row: that row is also what the
/// member write guard (<c>FOR KEY SHARE</c>) and erasure (<c>FOR UPDATE</c>) coordinate on, and a
/// device change has no business queuing behind an AI generator's write. The key is namespaced so
/// it cannot collide with another feature's advisory lock on the same id.
/// </remarks>
public static class DeviceMemberLock
{
    /// <summary>Takes the lock for <paramref name="cardiMemberId"/> until the current transaction ends.</summary>
    public static async Task AcquireAsync(DatabaseFacade database, Guid cardiMemberId, CancellationToken ct = default)
    {
        var key = $"device-connections:{cardiMemberId}";
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}
