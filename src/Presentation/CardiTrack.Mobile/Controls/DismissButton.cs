namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The app's one ✕ for putting something away — a card, a banner, a popup: a 32 tonal square at the
/// unified control radius, its ✕ in the tonal ink, inside a 48 tap band (the mobile spec's floor).
/// </summary>
/// <remarks>
/// The 2026-09-26 button audit found five of these — grey glyphs at 13, 14 and 15 points in two
/// greys, and a 20-point red square in the popups — for the one job. A search field's clear ✕ is a
/// different thing (it sits inside the field and empties it) and keeps its own quieter look.
/// </remarks>
public sealed class DismissButton : ContentView
{
    private readonly Border _face;

    public event EventHandler? Clicked;

    public DismissButton()
    {
        WidthRequest = 48;
        HeightRequest = 48;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Center;
        AutomationProperties.SetIsInAccessibleTree(this, true);
        SemanticProperties.SetDescription(this, "Dismiss");
        SemanticProperties.SetHint(this, "Double tap to dismiss");

        _face = new Border
        {
            WidthRequest = 32,
            HeightRequest = 32,
            StrokeThickness = 0,
            BackgroundColor = ControlResources.Color("ActionTintBlueBackground", Colors.AliceBlue),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new Label
            {
                Text = "✕",
                FontFamily = "QuicksandSemiBold",
                FontSize = 13,
                TextColor = ControlResources.Color("ActionTintBlueInk", Colors.SteelBlue),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };

        // The tap on the control itself — the node a screen reader focuses and activates.
        var band = new Grid { BackgroundColor = Colors.Transparent, Children = { _face } };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (!IsEnabled)
                return;
            Clicked?.Invoke(this, EventArgs.Empty);
            await _face.FadeToAsync(0.7, 70);
            await _face.FadeToAsync(1, 120);
        };
        GestureRecognizers.Add(tap);
        Content = band;
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled))
            _face.Opacity = IsEnabled ? 1 : 0.45;
    }
}
