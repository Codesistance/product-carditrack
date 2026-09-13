using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The Google-mandated health-data disclosure: one sentence, in Google's prescribed form, saying
/// what CardiTrack collects and why, with a way to read more and a way to say "seen". Shown to
/// a signed-in caregiver until they dismiss it, and the dismissal is recorded on the account —
/// the first acknowledgement's timestamp is the compliance record — so it does not come back on
/// another device either. The mobile twin of the web app's <c>HealthDataDisclosureBanner</c>.
/// </summary>
/// <remarks>
/// The page owns the two decisions this raises — where "Learn more" goes and what dismissal
/// means — so the control raises events rather than navigating or calling the API itself; that
/// is what lets it sit on any page that shows health data, not just the dashboard.
/// </remarks>
public sealed class HealthDataDisclosureBanner : Border
{
    /// <summary>Google's prescribed wording, verbatim — the same string the web banner shows.</summary>
    public const string Copy =
        "CardiTrack collects health and fitness data to enable anomaly alerts, daily health digests, and trend monitoring.";

    public const string LearnMoreText = "Learn more";

    private readonly Button _dismiss;

    /// <summary>The caregiver tapped "Learn more".</summary>
    public event EventHandler? LearnMoreRequested;

    /// <summary>The caregiver tapped the close control. The page decides when it actually hides.</summary>
    public event EventHandler? DismissRequested;

    public HealthDataDisclosureBanner()
    {
        IsVisible = false;
        StrokeThickness = 0;
        Padding = new Thickness(14, 10);
        Margin = new Thickness(20, 20, 20, 0);
        StrokeShape = new RoundRectangle { CornerRadius = 12 };
        BackgroundColor = ControlResources.Color("InfoBannerBackground", Color.FromArgb("#E7F1FB"));
        SemanticProperties.SetDescription(this, "Health data disclosure");

        var learnMore = new Span
        {
            Text = LearnMoreText,
            FontAttributes = FontAttributes.Bold,
            TextColor = ControlResources.Color("Primary", Color.FromArgb("#1B6EC2")),
        };
        learnMore.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => LearnMoreRequested?.Invoke(this, EventArgs.Empty)),
        });

        var text = new Label
        {
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center,
            TextColor = ControlResources.Color("HeadingText", Colors.Black),
            FormattedText = new FormattedString
            {
                Spans =
                {
                    new Span { Text = Copy + " " },
                    learnMore,
                },
            },
        };
        ControlResources.ApplyStyle(text, "Body2");

        _dismiss = new Button
        {
            Text = "✕",
            FontSize = 14,
            TextColor = ControlResources.Color("BodyText", Colors.DarkGray),
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(6, 0),
            MinimumWidthRequest = 32,
            MinimumHeightRequest = 32,
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(_dismiss, "Dismiss the health data disclosure");
        _dismiss.Clicked += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 10,
        };
        grid.Add(text, 0);
        grid.Add(_dismiss, 1);
        Content = grid;
    }

    /// <summary>Holds the close control while the dismissal is being recorded, so a second tap cannot race the first.</summary>
    public void SetBusy(bool busy) => _dismiss.IsEnabled = !busy;
}
