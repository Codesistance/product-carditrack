namespace CardiTrack.Mobile.Core.Journal;

/// <summary>How soon an entry asked the family to act — one urgency, or any.</summary>
public enum JournalUrgencyChoice
{
    Any,
    Watch,
    CheckIn,
    Concerning,
    ActNow,
}

/// <summary>How far back the journal list reaches, counted in the caregiver's own days.</summary>
public enum JournalWindow
{
    AllTime,
    Last7Days,
    Last30Days,
    Last90Days,
}

/// <summary>One part of a journal filter, as the applied-filter strip shows and removes it.</summary>
public enum JournalFilterPart
{
    Urgency,
    Window,
}

/// <summary>
/// The CardiJournal list's filter: how soon an entry asked for attention, and since when. Both
/// combine, and both combine with the search box — the journal endpoint takes them together.
/// </summary>
/// <remarks>
/// Whose journal it is sits outside this record on purpose. The list is always one member's, so
/// the member is not a narrowing that can be removed back to "everyone" the way the Alerts
/// filter's is (<c>AlertListFilter</c>) — it is which book is open, and the page names it rather
/// than offering it as a pill.
/// </remarks>
public sealed record JournalListFilter(
    JournalUrgencyChoice Urgency = JournalUrgencyChoice.Any,
    JournalWindow Window = JournalWindow.AllTime)
{
    /// <summary>Nothing narrowed: every entry, of any urgency, from any time.</summary>
    public static JournalListFilter None { get; } = new();

    public bool IsNarrowed => Parts().Count > 0;

    /// <summary>
    /// The filter as the journal endpoint's query: the urgency wire word and the first day to
    /// include. The window counts whole local days including today, so "Last 7 days" is today and
    /// the six before it.
    /// </summary>
    public (string? Urgency, DateOnly? From) ToQuery(DateOnly today)
    {
        int? days = Window switch
        {
            JournalWindow.Last7Days => 7,
            JournalWindow.Last30Days => 30,
            JournalWindow.Last90Days => 90,
            _ => null,
        };

        return (UrgencyWireValue(Urgency), days is { } d ? today.AddDays(-(d - 1)) : null);
    }

    /// <summary>
    /// The wire word for an urgency — the same vocabulary the entries come back carrying, so the
    /// sheet's colour dots can be looked up the way the cards' rails are. Null for "any".
    /// </summary>
    public static string? UrgencyWireValue(JournalUrgencyChoice urgency) => urgency switch
    {
        JournalUrgencyChoice.Watch => "watch",
        JournalUrgencyChoice.CheckIn => "check-in",
        JournalUrgencyChoice.Concerning => "concerning",
        JournalUrgencyChoice.ActNow => "act-now",
        _ => null,
    };

    /// <summary>
    /// The parts narrowing the list, each with the words the strip and the header subtitle use, in
    /// the sheet's own order. Empty when nothing is narrowed.
    /// </summary>
    public IReadOnlyList<(JournalFilterPart Part, string Label)> Parts()
    {
        var parts = new List<(JournalFilterPart, string)>(2);

        if (Urgency != JournalUrgencyChoice.Any)
            parts.Add((JournalFilterPart.Urgency, UrgencyLabel(Urgency)));
        if (Window != JournalWindow.AllTime)
            parts.Add((JournalFilterPart.Window, WindowLabel(Window)));

        return parts;
    }

    /// <summary>This filter with one part put back to its widest.</summary>
    public JournalListFilter Without(JournalFilterPart part) => part switch
    {
        JournalFilterPart.Urgency => this with { Urgency = JournalUrgencyChoice.Any },
        JournalFilterPart.Window => this with { Window = JournalWindow.AllTime },
        _ => this,
    };

    /// <summary>The words the entry cards' urgency uses, in sentence case.</summary>
    public static string UrgencyLabel(JournalUrgencyChoice urgency) => urgency switch
    {
        JournalUrgencyChoice.Watch => "Watch",
        JournalUrgencyChoice.CheckIn => "Check in",
        JournalUrgencyChoice.Concerning => "Concerning",
        JournalUrgencyChoice.ActNow => "Act now",
        _ => "Any",
    };

    public static string WindowLabel(JournalWindow window) => window switch
    {
        JournalWindow.Last7Days => "Last 7 days",
        JournalWindow.Last30Days => "Last 30 days",
        JournalWindow.Last90Days => "Last 90 days",
        _ => "All time",
    };
}
