using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>The Alerts List filter chips (M1-10a): All · Unread · Critical · Today · This Week.</summary>
public enum AlertFilter
{
    All,
    Unread,
    Critical,
    Today,
    ThisWeek,
}

/// <summary>
/// Horizontally scrolling chip row (Figma 101:6525). Built in code rather than XAML because
/// every chip is the same thing five times over, and the selected one swaps its whole fill.
/// </summary>
public sealed class FilterChipBar : ContentView
{
    private static readonly (AlertFilter Filter, string Label)[] Chips =
    [
        (AlertFilter.All, "All"),
        (AlertFilter.Unread, "Unread"),
        (AlertFilter.Critical, "Critical"),
        (AlertFilter.Today, "Today"),
        (AlertFilter.ThisWeek, "This Week"),
    ];

    private readonly Dictionary<AlertFilter, (Border Chip, Label Text)> _chips = new();

    /// <summary>Raised only when the selection actually changes — re-tapping a chip is a no-op.</summary>
    public event EventHandler<AlertFilter>? FilterChanged;

    public AlertFilter Selected { get; private set; } = AlertFilter.All;

    public FilterChipBar()
    {
        var row = new HorizontalStackLayout { Spacing = 9, Padding = new Thickness(20, 0) };

        foreach (var (filter, label) in Chips)
        {
            var text = new Label
            {
                Text = label,
                FontFamily = "QuicksandSemiBold",
                FontSize = 16,
                VerticalTextAlignment = TextAlignment.Center,
            };

            var chip = new Border
            {
                Padding = new Thickness(20, 9),
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                Content = text,
                Shadow = new Shadow
                {
                    Brush = Resource<Brush>("CardShadowBrush"),
                    Opacity = 0.15f,
                    Radius = 7,
                    Offset = new Point(0, 4),
                },
            };

            var captured = filter;
            chip.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Select(captured)) });

            _chips[filter] = (chip, text);
            row.Add(chip);
        }

        Content = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = row,
        };

        Paint();
    }

    public void Select(AlertFilter filter)
    {
        if (Selected == filter)
            return;

        Selected = filter;
        Paint();
        FilterChanged?.Invoke(this, filter);
    }

    /// <summary>
    /// Restores a selection without announcing it — for the page reapplying its own state,
    /// which must not look like the caregiver tapped a chip.
    /// </summary>
    public void SetSelectedSilently(AlertFilter filter)
    {
        Selected = filter;
        Paint();
    }

    private void Paint()
    {
        foreach (var (filter, (chip, text)) in _chips)
        {
            var isSelected = filter == Selected;

            chip.Background = isSelected ? Resource<Brush>("GradientButtonBrush") : null;
            chip.BackgroundColor = isSelected ? null : Resource<Color>("White");
            chip.Stroke = isSelected ? null : Resource<Color>("PrimaryDark");
            chip.StrokeThickness = isSelected ? 0 : 0.5;
            text.TextColor = Resource<Color>(isSelected ? "White" : "HeadingText");
        }
    }

    private static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}
