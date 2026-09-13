namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Pulls every default read the screens ask for into the encrypted on-device cache, so the
/// next landing paints immediately. The screens themselves still revalidate; this is what
/// makes that revalidate a replacement of something already on the wall rather than a wait.
/// </summary>
public interface IOfflineCacheWarmer
{
    /// <summary>
    /// No-ops when there is no local session — a signed-out device cannot call the API, and
    /// the cache is wiped on sign-out anyway. Concurrent calls share one run so a push, a tap
    /// and a login cannot stack three identical storms.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
