namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Which of the CardiJournal's books the app is reading.
/// </summary>
/// <remarks>
/// The app's own name for a series, kept separate from the wire token so a screen switching
/// cadence never has to hold a magic string. All three books' generators now exist, so every
/// value here selects a series something actually writes.
/// </remarks>
public enum JournalCadence
{
    /// <summary>One finished day.</summary>
    Daybook,

    /// <summary>One finished week, dated by the week's last day.</summary>
    Weekbook,

    /// <summary>One finished calendar month, dated by the month's last day.</summary>
    Monthbook,
}

/// <summary>Wire vocabulary for <see cref="JournalCadence"/>, and the words a caregiver reads.</summary>
public static class JournalCadenceExtensions
{
    /// <summary>The <c>?audience=</c> value the insights endpoints expect.</summary>
    public static string WireValue(this JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "weekbook",
        JournalCadence.Monthbook => "monthbook",
        _ => "daybook",
    };

    /// <summary>What one entry of this cadence is called, singular, as a caregiver would say it.</summary>
    public static string EntryName(this JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "Weekbook",
        JournalCadence.Monthbook => "Monthbook",
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
        JournalCadence.Monthbook => "Months",
        _ => "Days",
    };

    /// <summary>Parses a cadence back from its wire value, defaulting to the Daybook.</summary>
    public static JournalCadence ParseCadence(string? wireValue) => wireValue?.Trim().ToLowerInvariant() switch
    {
        "weekbook" => JournalCadence.Weekbook,
        "monthbook" => JournalCadence.Monthbook,
        _ => JournalCadence.Daybook,
    };
}
