using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One card per metric that has moved: how it is graded, where it sits, what that was judged
/// against, and an alert on it.
/// </summary>
/// <remarks>
/// <para>
/// A card rather than a row, unlike <see cref="FindingsList"/> next door, because each of these
/// carries something to do. A list marks points a caregiver reads; these ask a question — "do you
/// want to be told when this happens again?" — and a button on a bullet row reads as a stray
/// control rather than as part of the point it belongs to.
/// </para>
/// <para>
/// Everything drawn here is decided on the server. The icon comes from <c>MovementValence</c>, the
/// sentence from <c>BaselineMovementCalculator.Headline</c>, and the line under it from the
/// grading's own basis — so the figures are rounded and worded in one place, and this control
/// never works out a direction or a percentage of its own.
/// </para>
/// <para>
/// <b>The basis line is not decoration.</b> Two of the six metrics are graded against a range
/// somebody published and four against a convention of ours, and that line is the only place a
/// caregiver can tell the difference. A pass that tidies it away for being small print would
/// leave six identical-looking verdicts, two of which cite the AHA or the NSF and four of which
/// cite us.
/// </para>
/// </remarks>
public sealed class MovementCards : VerticalStackLayout
{
    /// <summary>Between cards. Wider than a list's rows, because each of these is its own object.</summary>
    private const int BetweenCards = 10;

    /// <summary>The card's own padding and corner — a component radius, never a page one.</summary>
    private const int CardPadding = 14;
    private const int CardRadius = 12;

    /// <summary>The valence mark, at the size <see cref="FindingsList"/> draws its own.</summary>
    private const int MarkerSize = 20;
    private const int MarkerDrop = 1;
    private const int AfterMarker = 10;

    /// <summary>Between the headline, its basis, and the button under them.</summary>
    private const int WithinCard = 4;
    private const int BeforeButton = 10;

    /// <summary>
    /// What a caregiver taps to watch this metric. Given the member and the metric it was raised
    /// from, so the alarm form opens on the reading they were just shown.
    /// </summary>
    public Func<MemberMovementResponse, Task>? OnCreateAlert { get; set; }

    public MovementCards()
    {
        Spacing = BetweenCards;
    }

    /// <summary>
    /// Shows one card per movement, or hides the whole thing when nothing has moved.
    /// </summary>
    public void Apply(IReadOnlyList<MemberMovementResponse>? movements)
    {
        Children.Clear();

        var shown = (movements ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Headline))
            .ToList();

        IsVisible = shown.Count > 0;

        foreach (var movement in shown)
            Children.Add(Card(movement));
    }

    private Border Card(MemberMovementResponse movement)
    {
        var body = new VerticalStackLayout { Spacing = WithinCard };
        body.Add(Row(movement));

        if (!string.IsNullOrWhiteSpace(movement.Basis))
        {
            body.Add(new Label
            {
                Text = movement.Basis,
                // Compact rather than Caption. Caption is MutedText, which is the ink for a
                // date under a card — this line says whether a grade came from the AHA or from
                // us, which is the one thing on the card a caregiver cannot work out for
                // themselves, and it was the palest text on it.
                Style = NamedStyle("Compact"),
                LineBreakMode = LineBreakMode.WordWrap,
                // Under the marker's column as well as the headline's, because it qualifies the
                // whole card rather than continuing the sentence above it.
                Margin = new Thickness(MarkerSize + AfterMarker, 0, 0, 0),
            });
        }

        // Only where there is an alarm to create. Active minutes has none — no alarm watches that
        // reading — and a button that opened a form on a different metric would set a caregiver
        // watching a figure other than the one they were just shown.
        if (movement.AlarmMetric is not null)
            body.Add(AlertButton(movement));

        return new Border
        {
            Content = body,
            Padding = CardPadding,
            StrokeThickness = 1,
            Stroke = NamedColor("CardOutline") ?? Colors.Transparent,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
            {
                CornerRadius = CardRadius,
            },
        };
    }

    private static Grid Row(MemberMovementResponse movement)
    {
        // Top-aligned against a headline that wraps, the way the list's marker is: a mark on the
        // vertical centre of a two-line sentence floats in the middle of it.
        var marker = new Image
        {
            Source = Marker(movement.Valence),
            WidthRequest = MarkerSize,
            HeightRequest = MarkerSize,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, MarkerDrop, 0, 0),
        };

        // The grading is said in the headline and in the basis under it, so the mark repeats
        // rather than adds. A screen reader announcing "warning" before every card would put a
        // word in front of the sentence that the sentence already carries.
        AutomationProperties.SetIsInAccessibleTree(marker, false);

        var headline = new Label
        {
            Text = movement.Headline,
            Style = NamedStyle("Body2Medium"),
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            ],
            ColumnSpacing = AfterMarker,
        };

        row.Add(marker);
        row.Add(headline, column: 1);
        return row;
    }

    private View AlertButton(MemberMovementResponse movement)
    {
        var button = new Button
        {
            Text = "Set up an alert",
            Style = NamedStyle("CardActionButton"),
            HorizontalOptions = LayoutOptions.Start,
            Margin = new Thickness(MarkerSize + AfterMarker, BeforeButton - WithinCard, 0, 0),
        };

        // Named for the metric rather than for the button, because "Set up an alert" repeated down
        // a card stack tells a screen reader nothing about which one it would be setting up.
        AutomationProperties.SetName(button, $"Set up an alert for {movement.Label}");

        button.Clicked += async (_, _) =>
        {
            if (OnCreateAlert is { } handler)
                await handler(movement);
        };

        return button;
    }

    /// <summary>
    /// The colour of the grade that dominates a set of movements, for a caller summarising them
    /// in one mark — the count badge on the section header.
    /// </summary>
    /// <remarks>
    /// The most common grade, and the more serious of two where they tie. A week with two things
    /// to look at and two that are fine is a week with something to look at: rounding that pair
    /// down to the favourable colour would let the badge say the opposite of half its contents.
    /// </remarks>
    public static Color? DominantTint(IReadOnlyList<MemberMovementResponse>? movements)
    {
        var dominant = (movements ?? [])
            .GroupBy(m => m.Valence)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => Seriousness(g.Key))
            .FirstOrDefault()?.Key;

        return NamedColor(dominant switch
        {
            "favourable" => "CountBadgeFavourable",
            "attention" => "CountBadgeAttention",
            _ => "CountBadgeNeutral",
        });
    }

    private static int Seriousness(string? valence) => valence switch
    {
        "attention" => 2,
        "neutral" => 1,
        _ => 0,
    };

    /// <summary>
    /// The mark for a grade. The vocabulary is the server's — see <c>MovementValence</c> — and an
    /// unrecognised one is drawn neutrally rather than not at all, so a card from a newer server
    /// still reads as a card.
    /// </summary>
    private static string Marker(string? valence) => valence switch
    {
        "favourable" => "icon_trend_good.svg",
        // Hands round a heart, not a caution triangle. A movement worth attention is a reason to
        // look after somebody, and the triangle — the same mark the account-deletion list uses for
        // what cannot be undone — read as an emergency on a card about a quieter week.
        "attention" => "icon_movement_attention.svg",
        _ => "icon_trend_neutral.svg",
    };

    /// <summary>
    /// The named style, or nothing where the dictionary has not loaded — a control that throws
    /// while its resources are still coming up would take the page down with it.
    /// </summary>
    private static Style? NamedStyle(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;

    private static Color? NamedColor(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Color
            : null;
}
