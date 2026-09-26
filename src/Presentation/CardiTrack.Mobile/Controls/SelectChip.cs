using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The app's one pick-one chip — a filter sheet's answers, a medical line's kind: 36 high at the
/// unified control radius, the set one filled in the action blue with white words, the rest white
/// with a hairline PrimaryDark outline. Which one is set is the caller's to say
/// (<see cref="IsSelected"/>); the chip only reports the tap.
/// </summary>
/// <remarks>
/// The 2026-09-26 popup pass found three hand-built chips for the one job: the filter sheet's
/// gradient pill, and the medical popups' 12-point squares — 32 high in one, 36 in the other, the
/// sort form's Skip filled OffBlack. One control, so a choice looks the same wherever it is made.
/// The tap is on the control itself, the node a screen reader focuses, and the set chip says so
/// in its hint.
/// </remarks>
public sealed class SelectChip : ContentView
{
    /// <summary>
    /// Widest the words get before they are cut short — for a CardiMember's long name in a filter,
    /// which would otherwise push a chip wider than the sheet.
    /// </summary>
    private const double MaxTextWidth = 200;

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(SelectChip), string.Empty,
        propertyChanged: (b, _, n) => ((SelectChip)b).ApplyText((string?)n));

    public static readonly BindableProperty IsSelectedProperty = BindableProperty.Create(
        nameof(IsSelected), typeof(bool), typeof(SelectChip), false,
        propertyChanged: (b, _, _) => ((SelectChip)b).ApplyLook());

    public static readonly BindableProperty DotProperty = BindableProperty.Create(
        nameof(Dot), typeof(Color), typeof(SelectChip), null,
        propertyChanged: (b, _, _) => ((SelectChip)b).ApplyDot());

    private readonly Border _face;
    private readonly Label _label;
    private readonly Ellipse _dot;

    /// <summary>Raised on a tap; the caller decides what is set and sets <see cref="IsSelected"/>.</summary>
    public event EventHandler? Tapped;

    public SelectChip()
    {
        AutomationProperties.SetIsInAccessibleTree(this, true);

        _label = new Label
        {
            FontFamily = "QuicksandSemiBold",
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaximumWidthRequest = MaxTextWidth,
        };

        _dot = new Ellipse
        {
            WidthRequest = 8,
            HeightRequest = 8,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };

        _face = new Border
        {
            MinimumHeightRequest = 36,
            Padding = new Thickness(14, 7),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children = { _dot, _label },
            },
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (IsEnabled)
                Tapped?.Invoke(this, EventArgs.Empty);
        };
        GestureRecognizers.Add(tap);

        Content = _face;
        ApplyLook();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>A colour shown before the words — a severity, an urgency. Null for none.</summary>
    public Color? Dot
    {
        get => (Color?)GetValue(DotProperty);
        set => SetValue(DotProperty, value);
    }

    /// <summary>
    /// The words, and the words as the chip's name for a screen reader. A caller that needs a
    /// fuller name ("Allergy: penicillin") sets its own description after the text.
    /// </summary>
    private void ApplyText(string? text)
    {
        _label.Text = text;
        SemanticProperties.SetDescription(this, text);
    }

    private void ApplyDot()
    {
        _dot.Fill = Dot is { } colour ? new SolidColorBrush(colour) : Brush.Transparent;
        _dot.IsVisible = Dot is not null;
    }

    private void ApplyLook()
    {
        var on = IsSelected;
        _face.Background = on
            ? ControlResources.Brush("ActionBlueBrush")
              ?? new SolidColorBrush(ControlResources.Color("Primary", Colors.SteelBlue))
            : null;
        _face.BackgroundColor = on ? null : ControlResources.Color("White", Colors.White);
        _face.Stroke = on
            ? null
            : new SolidColorBrush(ControlResources.Color("PrimaryDark", Colors.DarkSlateBlue));
        _face.StrokeThickness = on ? 0 : 0.5;
        _label.TextColor = on
            ? ControlResources.Color("White", Colors.White)
            : ControlResources.Color("HeadingText", Colors.Black);
        SemanticProperties.SetHint(this, on ? "Selected" : string.Empty);
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled))
            _face.Opacity = IsEnabled ? 1 : 0.45;
    }
}
