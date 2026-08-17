namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Last-known-good store for API GET bodies. Writes must be encrypted at rest — this holds
/// wearer health data, and a convenience feature that degrades to plaintext is worse than
/// no cache. Implementations that cannot encrypt must no-op rather than write in the clear.
/// </summary>
public interface IOfflineReadCache
{
    /// <summary>How long a snapshot stays eligible to show. Older entries are dropped on read.</summary>
    static TimeSpan Lifetime => TimeSpan.FromDays(7);

    Task SaveAsync(string key, string payload, CancellationToken ct = default);

    Task<OfflineCacheEntry?> TryGetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Drops a single entry. Distinct from <see cref="ClearAsync"/>, which is sign-out — that
    /// wipes every snapshot and rotates the key, far too much for a caller that has merely
    /// learned one entry is no longer worth keeping.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

public sealed record OfflineCacheEntry(string Payload, DateTimeOffset CachedAt);
