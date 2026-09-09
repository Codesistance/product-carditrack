namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// What a screen must be told, in order, while <see cref="SnapshotRefresh"/> runs — so that a
/// caregiver reading saved health data always knows it is saved, and sees the moment it is
/// replaced by something current.
/// </summary>
public interface IRefreshFeedback
{
    /// <summary>
    /// A saved snapshot is about to be rendered and the live call will run behind it.
    /// <paramref name="savedAt"/> is when the snapshot was written, or null if the store could
    /// not say. Raised before the render, so the screen is never showing saved data unmarked.
    /// </summary>
    void SavedShown(DateTimeOffset? savedAt);

    /// <summary>
    /// Called immediately before the fresh render replaces a snapshot the caregiver has been
    /// reading. The overlay goes up and the saved-data mark comes down here, so the
    /// replacement happens under the one and the fresh render is not drawn as saved.
    /// </summary>
    void Replacing();

    /// <summary>Always last, exactly once per run that was not superseded.</summary>
    void Completed(RefreshOutcome outcome);
}
