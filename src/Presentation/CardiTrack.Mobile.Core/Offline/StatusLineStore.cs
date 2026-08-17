using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// The live status line last shown for a CardiMember, together with the severity tier it was
/// generated under.
/// </summary>
/// <remarks>
/// The tier is stored because the server does not send one back: <c>CurrentStatusMessageResponse</c>
/// carries only a headline, a message and a timestamp, and the tier reaches the model through the
/// prompt alone. Without it a sentence written about a steady day could be put back on a card that
/// has since gone orange, which on a monitoring screen is worse than any placeholder.
/// </remarks>
public sealed record StoredStatusLine(
    string HealthStatus,
    string? Headline,
    string Message,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Last-known-good status line per member, so a screen that is being rebuilt can show the answer
/// it already has instead of a loading placeholder.
/// </summary>
public interface IStatusLineStore
{
    Task SaveAsync(Guid cardiMemberId, StoredStatusLine line, CancellationToken ct = default);

    /// <summary>
    /// The stored line, or null when there is nothing worth showing — nothing saved, a line
    /// written under a different <paramref name="healthStatus"/>, or one older than
    /// <paramref name="maxAge"/>.
    /// </summary>
    Task<StoredStatusLine?> TryGetAsync(
        Guid cardiMemberId, string healthStatus, TimeSpan maxAge, CancellationToken ct = default);

    Task ClearAsync(Guid cardiMemberId, CancellationToken ct = default);
}

/// <summary>
/// Kept in the encrypted offline cache rather than <c>Preferences</c>: a sentence about how
/// somebody's heart is doing today is wearer health data, and this store inherits the cache's
/// encryption at rest, its fail-closed behaviour when the platform keystore is unavailable, and
/// its wipe on sign-out.
/// </summary>
/// <remarks>
/// Every method swallows its own failures. This exists to spare a caregiver a placeholder — it is
/// never a reason to fail the screen it decorates.
/// </remarks>
public sealed class OfflineStatusLineStore : IStatusLineStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IOfflineReadCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<OfflineStatusLineStore> _logger;

    public OfflineStatusLineStore(
        IOfflineReadCache cache,
        TimeProvider? clock = null,
        ILogger<OfflineStatusLineStore>? logger = null)
    {
        _cache = cache;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OfflineStatusLineStore>.Instance;
    }

    public async Task SaveAsync(Guid cardiMemberId, StoredStatusLine line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        try
        {
            await _cache.SaveAsync(KeyFor(cardiMemberId), JsonSerializer.Serialize(line, Json), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status line write failed for member {CardiMemberId}", cardiMemberId);
        }
    }

    public async Task<StoredStatusLine?> TryGetAsync(
        Guid cardiMemberId, string healthStatus, TimeSpan maxAge, CancellationToken ct = default)
    {
        var key = KeyFor(cardiMemberId);

        OfflineCacheEntry? entry;
        try
        {
            entry = await _cache.TryGetAsync(key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status line read failed for member {CardiMemberId}", cardiMemberId);
            return null;
        }

        if (entry is null)
            return null;

        StoredStatusLine? line;
        try
        {
            line = JsonSerializer.Deserialize<StoredStatusLine>(entry.Payload, Json);
        }
        catch (JsonException ex)
        {
            // Written by an older build, or damaged. Drop it rather than leave a payload that
            // will fail the same way on every load for the next seven days.
            _logger.LogWarning(ex, "Stored status line for member {CardiMemberId} was unreadable; dropping it",
                cardiMemberId);
            await RemoveQuietlyAsync(key, ct);
            return null;
        }

        if (line is null || string.IsNullOrWhiteSpace(line.Message) || string.IsNullOrWhiteSpace(line.HealthStatus))
            return null;

        // A line describes the tier it was written about. Once the member has moved, it is a
        // statement about a day that is no longer on screen.
        if (!string.Equals(line.HealthStatus, healthStatus, StringComparison.Ordinal))
            return null;

        return _clock.GetUtcNow() - line.GeneratedAt > maxAge ? null : line;
    }

    public Task ClearAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        RemoveQuietlyAsync(KeyFor(cardiMemberId), ct);

    private async Task RemoveQuietlyAsync(string key, CancellationToken ct)
    {
        try
        {
            await _cache.RemoveAsync(key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status line delete failed for {CacheKey}", key);
        }
    }

    /// <summary>
    /// Prefixed so it cannot collide with the request paths the cache is otherwise keyed by —
    /// this entry is ours, not a response body.
    /// </summary>
    private static string KeyFor(Guid cardiMemberId) => $"local/status-line/{cardiMemberId}";
}
