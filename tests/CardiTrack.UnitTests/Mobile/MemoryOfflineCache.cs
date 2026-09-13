using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>In-memory stand-in for the encrypted file cache: same contract, inspectable entries.</summary>
internal sealed class MemoryOfflineCache : IOfflineReadCache
{
    private readonly object _gate = new();

    public Dictionary<string, OfflineCacheEntry> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every <see cref="RemoveAsync"/> throws it — eviction must stay best-effort.</summary>
    public Exception? RemoveThrows { get; set; }

    public Task SaveAsync(string key, string payload, CancellationToken ct = default)
    {
        lock (_gate)
            Items[key] = new OfflineCacheEntry(payload, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<OfflineCacheEntry?> TryGetAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Items.TryGetValue(key, out var entry) ? entry : null);
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (RemoveThrows is { } ex)
            throw ex;
        lock (_gate)
            Items.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
            Items.Clear();
        return Task.CompletedTask;
    }
}
