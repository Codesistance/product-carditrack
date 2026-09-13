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

    /// <summary>
    /// Cancels the shared run and waits for its in-flight GETs to stop writing. The caller
    /// token does not abort that wait — sign-out must not wipe tokens and the cache while
    /// a GET can still complete. Call this before clearing either, otherwise a late
    /// completion can put the previous caregiver's answers back under the next session's key.
    /// </summary>
    Task DrainForSignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Allows <see cref="RefreshAsync"/> again after the local session has been wiped (or a
    /// new one has been saved). Drain leaves the warmer closed so a push that arrives in that
    /// gap cannot start another storm.
    /// </summary>
    void ResumeAfterSignOut();
}
