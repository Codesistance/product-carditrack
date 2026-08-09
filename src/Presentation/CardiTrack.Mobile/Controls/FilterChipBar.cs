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
/// Horizontally scrolling chip row. Built in code rather than XAML because every chip is the
/// same thing five times over, and the selected one swaps its whole fill and its caret.
/// </summary>
/// <remarks>
/// The chip set is 101:6525's (All · Unread · Critical · Today · This Week), drawn in
/// 101:6206's style — rounded pill with a trailing caret, gradient fill when selected,
/// hairline #174E86 outline when not.
/// </remarks>
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

    private readonly Dictionary<AlertFilter, (Border Chip, Label Text, Image Caret)> _chips = new();

    /// <summary>
    /// The same filters the chips offer, in the same order — so the header's filter button can
    /// present them as a sheet in the states that have no chip row (Figma 101:3840).
    /// </summary>
    public static IReadOnlyList<(AlertFilter Filter, string Label)> Options => Chips;

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

            var caret = new Image
            {
                WidthRequest = 18,
                HeightRequest = 9,
                VerticalOptions = LayoutOptions.Center,
            };

            var chip = new Border
            {
                // Asymmetric by design: the caret needs less breathing room on the right
                // than the label does on the left.
                Padding = new Thickness(20, 9, 14, 9),
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                Content = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { text, caret },
                },
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

            _chips[filter] = (chip, text, caret);
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
        foreach (var (filter, (chip, text, caret)) in _chips)
        {
            var isSelected = filter == Selected;

            chip.Background = isSelected ? Resource<Brush>("GradientButtonBrush") : null;
            chip.BackgroundColor = isSelected ? null : Resource<Color>("White");
            chip.Stroke = isSelected ? null : Resource<Color>("PrimaryDark");
            chip.StrokeThickness = isSelected ? 0 : 0.5;
            text.TextColor = Resource<Color>(isSelected ? "White" : "HeadingText");
            // Two files rather than a tint: MAUI has no colour filter for SVG sources.
            caret.Source = isSelected ? "icon_caret_down_white.svg" : "icon_caret_down.svg";
        }
    }

    private static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}
