namespace CardiTrack.Mobile.Controls;

/// <summary>Which ground an <see cref="ExportAction"/> is sitting on.</summary>
public enum ExportActionTone
{
    /// <summary>On a page: the gradient the app gives its actions.</summary>
    Page = 0,

    /// <summary>On a <see cref="HeaderBand"/>, where that gradient is already the background.
    /// Wears the same translucent white the band's other buttons wear.</summary>
    Header = 1,
}

/// <summary>
/// "Take a copy of this away", wherever the app offers it — one glyph, one label, one geometry.
/// </summary>
/// <remarks>
/// <para>
/// This was four different things: a gradient pill on the journal list, a bare icon in the
/// journal entry's header, and a tonal pill and an outline button on the chat sheet. One action
/// read as four, and the single-journal case named itself only to a screen reader.
/// </para>
/// <para>
/// Two tones, because the control has to sit on two grounds — a page, and the header band whose
/// background *is* the app's action gradient. Same geometry, same words, same glyph either way;
/// only the fill changes, exactly as the app's other controls do when they cross onto the band.
/// </para>
/// <para>
/// Built in code rather than as a templated XAML control, following <see cref="ChatSeriesChart"/>
/// and <see cref="UpdatingOverlay"/>: every use sets literal values, so plain properties do the
/// work of bindable ones without a control template to keep in step with them.
/// </para>
/// </remarks>
public sealed class ExportAction : ContentView
{
    /// <summary>Type ramp and geometry shared with the journal list's pills, which this matched
    /// before it was a control and still has to sit beside.</summary>
    private const double LabelSize = 13;

    private const double GlyphSize = 17;

    private readonly Border _pill;
    private readonly Label _label;

    private ExportActionTone _tone = ExportActionTone.Page;

    public ExportAction()
    {
        _label = new Label
        {
            Text = "Export",
            FontFamily = "QuicksandSemiBold",
            FontSize = LabelSize,
            TextColor = ControlResources.Color("White", Colors.White),
            VerticalOptions = LayoutOptions.Center,
        };

        var glyph = new Image
        {
            Source = "icon_export_white.svg",
            WidthRequest = GlyphSize,
            HeightRequest = GlyphSize,
            VerticalOptions = LayoutOptions.Center,
        };
        // Decorative: the label beside it already says what this does, and a screen reader
        // reading both would say "export" twice.
        AutomationProperties.SetIsInAccessibleTree(glyph, false);

        _pill = new Border
        {
            Padding = new Thickness(14, 7),
            StrokeThickness = 0,
            MinimumHeightRequest = 40,
            Background = FillFor(_tone),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
            Content = new HorizontalStackLayout
            {
                Spacing = 7,
                VerticalOptions = LayoutOptions.Center,
                Children = { glyph, _label },
            },
        };

        Content = _pill;
        GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => Tapped?.Invoke(this, EventArgs.Empty)),
        });

        SemanticProperties.SetDescription(this, _label.Text);
    }

    /// <summary>Raised when the caregiver taps it.</summary>
    public event EventHandler? Tapped;

    /// <summary>
    /// What is being copied — "Export", "Export Range". The one thing that varies between uses,
    /// because it is the only part that can say what the copy will contain.
    /// </summary>
    public string Text
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            SemanticProperties.SetDescription(this, value);
        }
    }

    public ExportActionTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            _pill.Background = FillFor(value);
        }
    }

    private static Brush FillFor(ExportActionTone tone) => tone switch
    {
        // The band's own button tint. A gradient pill on the gradient band would be a shape you
        // can only find by knowing it is there.
        ExportActionTone.Header => new SolidColorBrush(
            ControlResources.Color("HeaderButtonTint", Color.FromArgb("#4AFFFFFF"))),
        _ => ControlResources.Brush("GradientButtonBrush")
            ?? new SolidColorBrush(ControlResources.Color("Primary", Color.FromArgb("#1884DC"))),
    };
}
