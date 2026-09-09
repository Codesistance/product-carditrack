using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// The screen-side half of <see cref="SnapshotRefresh"/>: routes what a run reports to the two
/// controls that show it — the saved-data banner for the whole time a snapshot is up, the
/// updating overlay for the second it is replaced. Either control may be absent; a screen with
/// no banner still gets the overlay, and <see cref="None"/> is for loads that show nothing.
/// </summary>
public sealed class RefreshFeedback : IRefreshFeedback
{
    public static readonly RefreshFeedback None = new(null, null);

    private readonly SavedDataBanner? _banner;
    private readonly UpdatingOverlay? _overlay;

    public RefreshFeedback(SavedDataBanner? banner, UpdatingOverlay? overlay)
    {
        _banner = banner;
        _overlay = overlay;
    }

    public void SavedShown(DateTimeOffset? savedAt) =>
        _banner?.Show(SavedDataState.Checking, savedAt);

    // Fire-and-forget on purpose: the run calls this immediately before the fresh render, and
    // awaiting the overlay's whole second here would hold that render up behind it.
    public void Replacing() =>
        _ = _overlay?.ShowAsync();

    public void Completed(RefreshOutcome outcome) =>
        _banner?.Apply(outcome);
}
