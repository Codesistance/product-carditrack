using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// App-resource lookups for controls built in code rather than XAML, where a StaticResource
/// extension is not available. Each takes a fallback so a control constructed before the app's
/// resources are merged — a designer, a test harness — still draws something sensible.
/// </summary>
internal static class ControlResources
{
    public static Color Color(string key, Color fallback) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Color colour
            ? colour
            : fallback;

    public static Brush? Brush(string key) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush
            ? brush
            : null;

    public static void ApplyStyle(VisualElement element, string key)
    {
        if (MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style)
            element.Style = style;
    }
}
