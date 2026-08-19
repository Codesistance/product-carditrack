namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Which of the CardiJournal's books the app is reading.
/// </summary>
/// <remarks>
/// The app's own name for a series, kept separate from the wire token so a screen switching
/// cadence never has to hold a magic string. <see cref="Monthbook"/> is deliberately absent: its
/// generator does not exist yet, and a cadence the app can select but nothing writes would be an
/// empty tab with no explanation. It arrives with the Monthbook itself.
/// </remarks>
public enum JournalCadence
{
    /// <summary>One finished day.</summary>
    Daybook,

    /// <summary>One finished week, dated by the week's last day.</summary>
    Weekbook,
}

/// <summary>Wire vocabulary for <see cref="JournalCadence"/>, and the words a caregiver reads.</summary>
public static class JournalCadenceExtensions
{
    /// <summary>The <c>?audience=</c> value the insights endpoints expect.</summary>
    public static string WireValue(this JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "weekbook",
        _ => "daybook",
    };

    /// <summary>What one entry of this cadence is called, singular, as a caregiver would say it.</summary>
    public static string EntryName(this JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "Weekbook",
        _ => "Daybook",
    };

    /// <summary>The segmented control's label for this cadence — a plain time word, not the book's name.</summary>
    /// <remarks>
    /// "Days" and "Weeks" rather than "Daybooks" and "Weekbooks": the control is scanned at a
    /// glance and the period is what distinguishes the segments. The book names carry on the
    /// cards and the entry pages, where there is room for them to teach themselves.
    /// </remarks>
    public static string SegmentLabel(this JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "Weeks",
        _ => "Days",
    };

    /// <summary>Parses a cadence back from its wire value, defaulting to the Daybook.</summary>
    public static JournalCadence ParseCadence(string? wireValue) =>
        string.Equals(wireValue?.Trim(), "weekbook", StringComparison.OrdinalIgnoreCase)
            ? JournalCadence.Weekbook
            : JournalCadence.Daybook;
}
