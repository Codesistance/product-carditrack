namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The short points under a generated narrative — one movement per row, bulleted.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than XAML, like <see cref="NudgeMiniRow"/> and <see cref="FilterChipBar"/>:
/// there is no template to speak of, and the rows arrive from the server a handful at a time.
/// </para>
/// <para>
/// It replaces a single <c>Label</c> holding bullets joined with newlines, which was the cheaper
/// thing to write and cost three separate readability problems. A wrapped point began again under
/// its own bullet rather than under its text, so on the member card — where the list ran to seven
/// points — there was no way to see at a glance where one ended and the next began. Points could
/// not be spaced apart from each other without also spacing the lines inside them, because both
/// are line height in one label. And the whole block carried one style, which meant the caption
/// grey and 12pt of a timestamp.
/// </para>
/// <para>
/// So: a marker in its own column, the text in a column that wraps under itself, and body ink at
/// body size. These are the points a caregiver reads first — the narrative above them is the
/// detail — and they were the smallest, palest text on the card.
/// </para>
/// <para>
/// The rows are M1-08's, down to the twenty-pixel marker, the two-pixel drop and the twelve of
/// spacing either way. The marker is the design's own — a pale disc behind the tick — which the
/// app did not have: <c>icon_action_check.svg</c> is a bare stroke, so the onboarding page this
/// borrows from had been drawing the frame without its disc.
/// </para>
/// </remarks>
public sealed class FindingsList : VerticalStackLayout
{
    /// <summary>
    /// Between points, not inside them. A point that wraps stays one block while the list stays a
    /// list, which is the distinction the single label could not draw.
    /// </summary>
    /// <remarks>Twelve, the spacing the onboarding list these rows come from uses.</remarks>
    private const int BetweenPoints = 12;

    /// <summary>Between the marker and its text, matching the same list.</summary>
    private const int AfterMarker = 12;

    /// <summary>The marker's side, matching the same list.</summary>
    private const int MarkerSize = 20;

    /// <summary>
    /// Nudges the marker down onto the first line's optical centre rather than its box top.
    /// </summary>
    private const int MarkerDrop = 2;

    /// <summary>The marker every list uses unless it says otherwise.</summary>
    public const string CheckMarker = "icon_check_disc.svg";

    /// <summary>
    /// The marker for a list whose points are consequences rather than reassurances.
    /// </summary>
    /// <remarks>
    /// A caution mark, not a tick of any colour. A list of what deleting an account destroys is
    /// still a list and should be drawn like one, but a tick beside "nothing can be recovered
    /// afterwards" reads as approval of the sentence it marks, and a red tick reads as an
    /// emphatic one. The glyph is built like <c>icon_status_check</c> — the same circle at the
    /// same weight — carrying an exclamation in the danger palette instead of a tick.
    /// </remarks>
    public const string DangerMarker = "icon_caution_danger.svg";

    /// <summary>
    /// Which marker this list draws. <see cref="CheckMarker"/> unless set.
    /// </summary>
    public string MarkerSource { get; set; } = CheckMarker;

    public FindingsList()
    {
        Spacing = BetweenPoints;
    }

    /// <summary>
    /// Shows <paramref name="findings"/>, or hides the list when there are none.
    /// </summary>
    /// <remarks>
    /// Hidden rather than left empty: an empty list under a heading reads as something that failed
    /// to load, and a member with nothing worth listing is the ordinary case rather than a fault.
    /// </remarks>
    public void Apply(IReadOnlyList<string>? findings)
    {
        Children.Clear();

        var points = (findings ?? [])
            .Where(point => !string.IsNullOrWhiteSpace(point))
            .ToList();

        IsVisible = points.Count > 0;

        foreach (var point in points)
            Children.Add(Row(point.Trim(), MarkerSource));
    }

    private static Grid Row(string point, string markerSource)
    {
        // Its own column so the text beside it wraps under itself. Top-aligned rather than
        // centred: against a point that runs to three lines, a marker on the vertical centre
        // floats in the middle of the sentence.
        //
        // Decorative, and out of the accessibility tree: it marks a row rather than saying
        // anything, and a screen reader announcing "check mark" before each point would put a
        // word in front of every one of them that the list does not mean.
        var marker = new Image
        {
            Source = markerSource,
            WidthRequest = MarkerSize,
            HeightRequest = MarkerSize,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, MarkerDrop, 0, 0),
        };
        AutomationProperties.SetIsInAccessibleTree(marker, false);

        var text = new Label
        {
            Text = point,
            // No size of its own: Body2Medium is Message, which is the size and ink the narrative
            // above these points already uses. The onboarding frame sets its rows a point larger,
            // but there the list is the screen; here it sits under a paragraph, and a list that
            // outsizes the prose it belongs to reads as a second, louder voice.
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
        row.Add(text, column: 1);
        return row;
    }

    /// <summary>
    /// The named style, or nothing where the dictionary has not loaded — a control that throws
    /// while its resources are still coming up would take the page down with it.
    /// </summary>
    private static Style? NamedStyle(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;
}
