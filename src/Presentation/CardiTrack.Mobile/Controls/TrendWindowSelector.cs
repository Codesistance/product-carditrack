using Microsoft.Maui.Controls.Shapes;
// CardiTrack.Application shadows MAUI's Application in any file importing it — see NudgeMiniRow.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The 7 / 14 / 30-day window picker above the Member Detail screen's Key Metric Trends carousel.
/// A segmented control rather than chips: the three options are one exclusive choice over the same
/// axis, not independent filters, and only one can ever be on.
/// </summary>
/// <remarks>
/// Built in code rather than XAML, like <see cref="FilterChipBar"/> — it is the same segment three
/// times over, and the selected one swaps its whole fill.
/// </remarks>
public sealed class TrendWindowSelector : ContentView
{
    /// <summary>The window the screen opens on: a week reads at a glance, the longer ones are opt-in.</summary>
    public const int DefaultDays = 7;

    private static readonly int[] WindowDays = [7, 14, 30];

    /// <summary>Every window the picker offers, shortest first.</summary>
    public static IReadOnlyList<int> Windows => WindowDays;

    private const double SegmentHeight = 34;

    private readonly Dictionary<int, (Border Segment, Label Text)> _segments = new();

    /// <summary>Raised only when the selection actually changes — re-tapping a segment is a no-op.</summary>
    public event EventHandler<int>? WindowChanged;

    public int SelectedDays { get; private set; } = DefaultDays;

    public TrendWindowSelector()
    {
        var row = new Grid { ColumnSpacing = 4 };

        for (var i = 0; i < WindowDays.Length; i++)
        {
            var days = WindowDays[i];
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var text = new Label
            {
                Text = $"{days} days",
                FontFamily = "QuicksandSemiBold",
                FontSize = 14,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };

            var segment = new Border
            {
                HeightRequest = SegmentHeight,
                StrokeThickness = 0,
                Padding = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 11 },
                Content = text,
            };
            SemanticProperties.SetDescription(segment, $"Show the last {days} days");
            segment.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Select(days)) });

            _segments[days] = (segment, text);
            row.Add(segment, i);
        }

        Content = new Border
        {
            Padding = 4,
            StrokeThickness = 0,
            BackgroundColor = MetricStatus.Resource("InputBackground", Colors.WhiteSmoke),
            StrokeShape = new RoundRectangle { CornerRadius = 15 },
            Content = row,
        };

        Paint();
    }

    public void Select(int days)
    {
        if (SelectedDays == days || !_segments.ContainsKey(days))
            return;

        SelectedDays = days;
        Paint();
        WindowChanged?.Invoke(this, days);
    }

    private void Paint()
    {
        foreach (var (days, (segment, text)) in _segments)
        {
            var isSelected = days == SelectedDays;

            segment.Background = isSelected ? Resource<Brush>("GradientButtonBrush") : null;
            segment.BackgroundColor = isSelected ? null : Colors.Transparent;
            text.TextColor = MetricStatus.Resource(isSelected ? "White" : "BodyText", Colors.Gray);
        }
    }

    private static T Resource<T>(string key) =>
        (T)MauiApplication.Current!.Resources[key];
}
