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
/// hairline #174E86 outline when not. A named member chip can lead the row (see
/// <see cref="SetMemberFilter"/>); it is a second axis rather than a sixth option, so it sits
/// permanently selected beside whichever of the five is, and clears on its own tap.
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
    /// The leading "whose alerts" chip. Built once and shown or hidden, rather than added to and
    /// removed from the row, so the five behind it never re-layout under a caregiver's thumb.
    /// </summary>
    private readonly Border _memberChip;
    private readonly Label _memberChipText;

    /// <summary>Raised only when the selection actually changes — re-tapping a chip is a no-op.</summary>
    public event EventHandler<AlertFilter>? FilterChanged;

    /// <summary>Raised when the member chip is tapped, which is the only way to clear it.</summary>
    public event EventHandler? MemberFilterCleared;

    public AlertFilter Selected { get; private set; } = AlertFilter.All;

    /// <summary>
    /// Hides the five standard chips while leaving a member chip showing — for the archive
    /// listing, which none of the five apply to but which is still narrowed to one member. Without
    /// this the page would have to hide the whole row, and the archive would be quietly filtered
    /// to somebody with nothing on screen saying so.
    /// </summary>
    public bool StandardChipsVisible
    {
        get => _standardChipsVisible;
        set
        {
            _standardChipsVisible = value;
            foreach (var (chip, _, _) in _chips.Values)
                chip.IsVisible = value;
        }
    }

    private bool _standardChipsVisible = true;

    public FilterChipBar()
    {
        var row = new HorizontalStackLayout { Spacing = 9, Padding = new Thickness(20, 0) };

        (_memberChip, _memberChipText) = BuildMemberChip();
        row.Add(_memberChip);

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

    /// <summary>
    /// Drawn in the selected style and never in the unselected one: it is only ever on screen
    /// while it is doing something. A ✕ where the five carry a caret, because the caret means
    /// "there are choices behind this" and this chip's only other state is gone.
    /// </summary>
    private (Border Chip, Label Text) BuildMemberChip()
    {
        var text = new Label
        {
            FontFamily = "QuicksandSemiBold",
            FontSize = 16,
            TextColor = Resource<Color>("White"),
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            // A long name must not push the five off the row entirely.
            MaximumWidthRequest = 160,
        };

        var clear = new Label
        {
            Text = "✕",
            FontFamily = "QuicksandSemiBold",
            FontSize = 14,
            TextColor = Resource<Color>("White"),
            VerticalTextAlignment = TextAlignment.Center,
        };

        var chip = new Border
        {
            IsVisible = false,
            Padding = new Thickness(20, 9, 14, 9),
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Background = Resource<Brush>("GradientButtonBrush"),
            StrokeThickness = 0,
            Content = new HorizontalStackLayout
            {
                Spacing = 8,
                Children = { text, clear },
            },
            Shadow = new Shadow
            {
                Brush = Resource<Brush>("CardShadowBrush"),
                Opacity = 0.15f,
                Radius = 7,
                Offset = new Point(0, 4),
            },
        };

        chip.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => MemberFilterCleared?.Invoke(this, EventArgs.Empty)),
        });

        return (chip, text);
    }

    /// <summary>
    /// Shows the member chip under <paramref name="memberName"/>, or hides it when that is null.
    /// Announces nothing either way — the page owns the reload, since it is the one that knows
    /// whether the filter it is applying came from a tap or from the route it was opened on.
    /// </summary>
    public void SetMemberFilter(string? memberName)
    {
        _memberChip.IsVisible = !string.IsNullOrWhiteSpace(memberName);
        if (!_memberChip.IsVisible)
            return;

        _memberChipText.Text = memberName;
        SemanticProperties.SetDescription(_memberChip, $"Showing {memberName}'s alerts only");
        SemanticProperties.SetHint(_memberChip, "Tap to show every CardiMember's alerts");
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
