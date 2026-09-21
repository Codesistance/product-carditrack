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
/// So: a bullet in its own column, the text in a column that wraps under itself, and body ink at
/// body size. These are the points a caregiver reads first — the narrative above them is the
/// detail — and they were the smallest, palest text on the card.
/// </para>
/// </remarks>
public sealed class FindingsList : VerticalStackLayout
{
    /// <summary>
    /// Between points, not inside them. A point that wraps stays one block while the list stays a
    /// list, which is the distinction the single label could not draw.
    /// </summary>
    private const int BetweenPoints = 7;

    /// <summary>
    /// Between the bullet and its text. Wide enough that the glyph reads as a marker rather than
    /// as the first character of the sentence.
    /// </summary>
    private const int AfterBullet = 9;

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
            Children.Add(Row(point.Trim()));
    }

    private static Grid Row(string point)
    {
        // Its own column so the text beside it wraps under itself. Top-aligned rather than
        // centred: against a point that runs to three lines, a vertically centred bullet floats
        // in the middle of the sentence.
        var bullet = new Label
        {
            Text = "•",
            Style = NamedStyle("Body2Dark"),
            VerticalOptions = LayoutOptions.Start,
        };

        var text = new Label
        {
            Text = point,
            Style = NamedStyle("Body2Dark"),
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var row = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            ],
            ColumnSpacing = AfterBullet,
        };

        row.Add(bullet);
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
