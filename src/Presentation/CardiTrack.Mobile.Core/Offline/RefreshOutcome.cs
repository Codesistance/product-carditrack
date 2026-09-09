using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>How one <see cref="SnapshotRefresh"/> run ended, from the screen's point of view.</summary>
public enum RefreshResult
{
    /// <summary>Nothing was saved; the live call reached the API and its answer is on screen.</summary>
    FreshNoSnapshot,

    /// <summary>A saved snapshot was shown first; the live answer has now replaced it.</summary>
    FreshReplacedSaved,

    /// <summary>
    /// A saved snapshot was shown first and the live answer says nothing changed, so the
    /// screen was left alone — no second render, nothing to announce.
    /// </summary>
    FreshUnchanged,

    /// <summary>
    /// What is on screen came from the device: the API could not be reached, and either the
    /// client answered from its cache or the saved snapshot is all there is.
    /// </summary>
    SavedOnlyOffline,

    /// <summary>The API was reached and refused; the saved snapshot stays on screen.</summary>
    SavedOnlyHttpError,

    /// <summary>
    /// Nothing to show: there was no snapshot and the live call failed — or there was one and
    /// the API said the thing no longer exists, which a snapshot must not contradict. In that
    /// second case the snapshot <em>has been rendered</em>: the screen must now put its error
    /// state over it (and drop its own copy of the data), the same way it would for a cold
    /// failure. The run does not un-render — only the screen knows what its error state is.
    /// <see cref="RefreshOutcome.HasContent"/> is false for exactly this reason.
    /// </summary>
    NothingAndFailed,

    /// <summary>A newer load superseded this one; nothing was rendered or announced.</summary>
    Superseded,
}

/// <summary>
/// The result of one run, plus what the screen needs to say about it: when the data it is
/// showing was saved, and — when the live call failed — why.
/// </summary>
public sealed record RefreshOutcome(RefreshResult Result, DateTimeOffset? SavedAt, ApiException? Error)
{
    public static readonly RefreshOutcome Superseded = new(RefreshResult.Superseded, null, null);

    /// <summary>The live call reached the API; whatever is on screen is current.</summary>
    public bool IsFresh => Result is RefreshResult.FreshNoSnapshot
        or RefreshResult.FreshReplacedSaved
        or RefreshResult.FreshUnchanged;

    /// <summary>Something is on screen, fresh or saved.</summary>
    public bool HasContent => Result is not (RefreshResult.NothingAndFailed or RefreshResult.Superseded);

    /// <summary>What is on screen came from the device rather than the API.</summary>
    public bool IsSavedOnly => Result is RefreshResult.SavedOnlyOffline or RefreshResult.SavedOnlyHttpError;
}
