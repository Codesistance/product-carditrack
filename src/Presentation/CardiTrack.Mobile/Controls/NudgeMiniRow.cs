using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Notifications;
using Microsoft.Maui.Controls.Shapes;
// CardiTrack.Application (the DTO assembly's root namespace) shadows MAUI's Application in
// any file importing it, so the control type is aliased rather than qualified at each use.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The compact form of a notification, for the dashboard's two card slots and the safety banners.
/// </summary>
/// <remarks>
/// Built in code rather than XAML, like <see cref="FilterChipBar"/> and <see cref="SkeletonView"/>:
/// it is a single row with no template to speak of, and the dashboard creates it dynamically.
/// <para>
/// Deliberately headline-and-tap only. The dashboard is where a caregiver checks whether their
/// relative is alright — putting snooze and dismiss buttons in that flow would turn a health
/// glance into an admin queue. The full affordances live in the inbox.
/// </para>
/// </remarks>
public sealed class NudgeMiniRow : Border
{
    public event EventHandler<NotificationResponse>? Tapped;

    private readonly NotificationResponse _notification;

    public NudgeMiniRow(NotificationResponse notification, bool asSafetyBanner = false)
    {
        _notification = notification;

        StrokeThickness = 0;
        Padding = new Thickness(14, 12);
        StrokeShape = new RoundRectangle { CornerRadius = 12 };
        BackgroundColor = asSafetyBanner
            ? Resource("StaleBannerBackground", Colors.LightYellow)
            : Resource("InputBackground", Colors.WhiteSmoke);

        var accent = new BoxView
        {
            WidthRequest = 4,
            HeightRequest = 34,
            CornerRadius = 2,
            Color = AccentColour(notification),
            VerticalOptions = LayoutOptions.Center
        };

        var title = new Label
        {
            Text = NudgeCopy.Title(notification),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2,
            VerticalOptions = LayoutOptions.Center
        };
        ApplyStyle(title, asSafetyBanner ? "Body2Medium" : "Body2");

        var chevron = new Label
        {
            Text = "›",
            FontSize = 20,
            TextColor = Resource("MutedText", Colors.Gray),
            VerticalOptions = LayoutOptions.Center
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 12
        };
        grid.Add(accent, 0);
        grid.Add(title, 1);
        grid.Add(chevron, 2);

        Content = grid;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Tapped?.Invoke(this, _notification);
        GestureRecognizers.Add(tap);
    }

    private static Color AccentColour(NotificationResponse n)
    {
        var key = n.Category == NotificationCategory.Safety
            ? "StatusRed"
            : n.Priority switch
            {
                NotificationPriority.Critical => "StatusRed",
                NotificationPriority.High => "StatusOrange",
                NotificationPriority.Medium => "StatusYellow",
                _ => "Primary"
            };

        return Resource(key, Colors.Gray);
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
