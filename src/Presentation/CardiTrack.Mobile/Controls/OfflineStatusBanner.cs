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

    public void ApplyFrom(ICardiTrackApiClient api) => Apply(api.LastGetWasCached, api.LastCachedAt);

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
