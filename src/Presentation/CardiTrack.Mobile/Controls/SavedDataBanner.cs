using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>Why saved data is on screen, which decides what the banner says.</summary>
public enum SavedDataState
{
    Hidden,

    /// <summary>The device's saved snapshot is up and the live call is still running behind it.</summary>
    Checking,

    /// <summary>The API could not be reached; the snapshot is all there is.</summary>
    Offline,

    /// <summary>The API was reached and refused; the snapshot stays up.</summary>
    RefreshFailed,
}

/// <summary>
/// M4-03a compact read: last-known-good data is on screen, and this says so and why. A screen
/// showing saved health data must never let it pass for live — a caregiver reading a two-hour-old
/// heart rate as current is the failure this exists to prevent — so the banner is up for the
/// whole time a snapshot is, from the moment it is drawn until fresh data replaces it.
/// </summary>
/// <remarks>
/// Replaces the stale "pull down to check in" copy, which the app cannot honour when it is the
/// network that is missing. Formerly <c>OfflineStatusBanner</c>; being offline is now one of
/// three reasons the banner is up.
/// </remarks>
public sealed class SavedDataBanner : Border
{
    private readonly Label _label;

    public SavedDataState State { get; private set; }

    public SavedDataBanner()
    {
        IsVisible = false;
        StrokeThickness = 0;
        Padding = new Thickness(14, 10);
        StrokeShape = new RoundRectangle { CornerRadius = 12 };
        BackgroundColor = ControlResources.Color("StaleBannerBackground", Color.FromArgb("#FFF7E8"));

        _label = new Label
        {
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = ControlResources.Color("HeadingText", Colors.Black)
        };
        ControlResources.ApplyStyle(_label, "Body2");
        Content = _label;
    }

    /// <summary>Puts the banner up in <paramref name="state"/>, dated by when the snapshot was saved.</summary>
    public void Show(SavedDataState state, DateTimeOffset? savedAt)
    {
        if (state == SavedDataState.Hidden)
        {
            Hide();
            return;
        }

        State = state;
        _label.Text = CopyFor(state, savedAt);
        IsVisible = true;
    }

    public void Hide()
    {
        State = SavedDataState.Hidden;
        IsVisible = false;
    }

    /// <summary>
    /// The one mapping from how a load ended to what the banner says: up for the two saved-only
    /// outcomes, down for anything fresh, and down when there is nothing on screen at all — the
    /// error panel speaks then, and a banner about saved data over a panel saying there is none
    /// would contradict it.
    /// </summary>
    public void Apply(RefreshOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        switch (outcome.Result)
        {
            case RefreshResult.SavedOnlyOffline:
                Show(SavedDataState.Offline, outcome.SavedAt);
                break;
            case RefreshResult.SavedOnlyHttpError:
                Show(SavedDataState.RefreshFailed, outcome.SavedAt);
                break;
            case RefreshResult.Superseded:
                // A newer load owns the banner now.
                break;
            default:
                Hide();
                break;
        }
    }

    /// <summary>
    /// Reads the banner off the calls the screen actually loaded from — pass the tasks those
    /// calls returned. A screen with more than one load speaks for all of them: the banner is up
    /// if any came from the cache, and quotes the oldest of those, which is the weakest thing on
    /// screen and so the honest one to date the page by. For screens not yet on
    /// <see cref="SnapshotRefresh"/>; those use <see cref="Apply"/>.
    /// </summary>
    public void ApplyFrom(ICardiTrackApiClient api, params Task?[] calls)
    {
        ArgumentNullException.ThrowIfNull(api);
        DateTimeOffset? oldest = null;
        foreach (var call in calls)
        {
            if (call is not null
                && api.OriginOf(call)?.CachedAt is { } cachedAt
                && (oldest is null || cachedAt < oldest))
                oldest = cachedAt;
        }

        if (oldest is null)
            Hide();
        else
            Show(SavedDataState.Offline, oldest);
    }

    private static string CopyFor(SavedDataState state, DateTimeOffset? savedAt)
    {
        // "data saved 10 minutes ago", or just "saved data" when the store could not date it.
        var saved = savedAt is { } at
            ? $"data saved {RelativeTime.Format(at.UtcDateTime)}"
            : "saved data";

        return state switch
        {
            SavedDataState.Checking => $"Showing {saved} — checking for updates…",
            SavedDataState.RefreshFailed => $"Couldn't refresh — showing {saved}",
            _ => $"You're offline — showing {saved}",
        };
    }
}
