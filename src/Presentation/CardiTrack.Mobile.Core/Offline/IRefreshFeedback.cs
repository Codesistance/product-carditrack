namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// What a screen must be told, in order, while <see cref="SnapshotRefresh"/> runs — so that a
/// caregiver reading saved health data always knows it is saved, and sees the moment it is
/// replaced by something current.
/// </summary>
public interface IRefreshFeedback
{
    /// <summary>
    /// A saved snapshot has just been rendered and the live call is in flight.
    /// <paramref name="savedAt"/> is when the snapshot was written, or null if the store could
    /// not say.
    /// </summary>
    void SavedShown(DateTimeOffset? savedAt);

    /// <summary>
    /// Called immediately before the fresh render replaces a snapshot the caregiver has been
    /// reading. The overlay goes up here, so the replacement happens under it.
    /// </summary>
    void Replacing();

    /// <summary>Always last, exactly once per run that was not superseded.</summary>
    void Completed(RefreshOutcome outcome);
}
