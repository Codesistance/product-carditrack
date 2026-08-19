using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// M4-03a compact read: last-known-good data is on screen because the live call could not
/// reach the API. Replaces the stale "pull down to check in" copy, which we cannot honour.
/// </summary>
public sealed class OfflineStatusBanner : Border
{
    private readonly Label _label;

    public OfflineStatusBanner()
    {
        IsVisible = false;
        StrokeThickness = 0;
        Padding = new Thickness(14, 10);
        StrokeShape = new RoundRectangle { CornerRadius = 12 };
        BackgroundColor = Resource("StaleBannerBackground", Color.FromArgb("#FFF7E8"));

        _label = new Label
        {
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = Resource("HeadingText", Colors.Black)
        };
        ApplyStyle(_label, "Body2");
        Content = _label;
    }

    /// <summary>
    /// Reads the banner off the calls the screen actually loaded from — pass the tasks those
    /// calls returned. A screen with more than one load speaks for all of them: the banner is up
    /// if any came from the cache, and quotes the oldest of those, which is the weakest thing on
    /// screen and so the honest one to date the page by.
    /// </summary>
    public void ApplyFrom(ICardiTrackApiClient api, params Task?[] calls)
    {
        DateTimeOffset? oldest = null;
        foreach (var call in calls)
        {
            if (call is not null
                && api.OriginOf(call)?.CachedAt is { } cachedAt
                && (oldest is null || cachedAt < oldest))
                oldest = cachedAt;
        }

        Apply(oldest is not null, oldest);
    }

    public void Apply(bool offline, DateTimeOffset? cachedAt)
    {
        if (!offline || cachedAt is null)
        {
            IsVisible = false;
            return;
        }

        _label.Text = $"You're offline — showing data saved {RelativeTime.Format(cachedAt.Value.UtcDateTime)}";
        IsVisible = true;
    }

    private static Color Resource(string key, Color fallback) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Color colour
            ? colour
            : fallback;

    private static void ApplyStyle(Label label, string key)
    {
        if (MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style)
            label.Style = style;
    }
}
