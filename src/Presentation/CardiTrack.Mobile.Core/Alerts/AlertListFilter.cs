namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>Which open alerts the list shows, by where they are in being handled.</summary>
public enum AlertStatusChoice
{
    /// <summary>Everything nobody has closed — new and acknowledged alike.</summary>
    AllOpen,

    /// <summary>Raised and not yet acknowledged: the ones still waiting on a caregiver.</summary>
    NeedsResponse,

    /// <summary>Acknowledged by a caregiver, but the episode is not over.</summary>
    Acknowledged,
}

/// <summary>How serious an alert has to be to be listed — one severity, or any.</summary>
public enum AlertSeverityChoice
{
    Any,
    Critical,
    Urgent,
    Notice,
    Info,
}

/// <summary>How far back the list reaches, counted in the caregiver's own days.</summary>
public enum AlertWindow
{
    AnyTime,
    Today,
    Last7Days,
    Last30Days,
}

/// <summary>One part of a filter, as the applied-filter strip shows and removes it.</summary>
public enum AlertFilterPart
{
    Member,
    Status,
    Severity,
    Window,
}

/// <summary>
/// The Alerts list's filter: whose alerts, which of them, how serious, and since when. Every part
/// combines with every other — the alerts API takes a member, a status, a severity and a start
/// date together — so a caregiver can ask for "Pop's critical alerts that still need a response"
/// rather than picking one chip out of five.
/// </summary>
/// <remarks>
/// The member's name is carried beside the id only to be shown. The id is what the query is built
/// from, so a missing name costs the strip its wording, never the filter its meaning.
/// </remarks>
public sealed record AlertListFilter(
    Guid? MemberId = null,
    string? MemberName = null,
    AlertStatusChoice Status = AlertStatusChoice.AllOpen,
    AlertSeverityChoice Severity = AlertSeverityChoice.Any,
    AlertWindow Window = AlertWindow.AnyTime)
{
    /// <summary>
    /// The status that asks for every open alert. The same value the offline cache warms, so the
    /// unfiltered list peeks the page the device already saved.
    /// </summary>
    public const string OpenStatus = Offline.OfflineReadDefaults.OpenAlertStatus;

    public const string NeedsResponseStatus = "new";

    public const string AcknowledgedStatus = "acknowledged";

    /// <summary>The archive: alerts whose episode is over.</summary>
    public const string ResolvedStatus = "resolved";

    /// <summary>What a member part says when the route narrowed the list but carried no name.</summary>
    public const string UnnamedMemberLabel = "This CardiMember";

    /// <summary>Nothing narrowed: every open alert, for everyone, of any severity, from any time.</summary>
    public static AlertListFilter None { get; } = new();

    /// <summary>
    /// How many parts are narrowing the list, for the header button's count. The status part does
    /// not count in the archive, where it does not apply.
    /// </summary>
    public int ActiveCount(bool archived) => Parts(archived).Count;

    public bool IsNarrowed(bool archived) => ActiveCount(archived) > 0;

    /// <summary>
    /// The filter as the alerts API's query: <c>severity</c>, <c>status</c> and <c>from</c>. The
    /// archive swaps the status for resolved and keeps the rest — it is a different list, but it
    /// can still be Pop's, or only the critical ones, or only this week's.
    /// </summary>
    /// <param name="today">The caregiver's local midnight: "Today" has to mean their today, not UTC's.</param>
    public (string? Severity, string Status, DateTime? From) ToQuery(DateTime today, bool archived)
    {
        var status = archived
            ? ResolvedStatus
            : Status switch
            {
                AlertStatusChoice.NeedsResponse => NeedsResponseStatus,
                AlertStatusChoice.Acknowledged => AcknowledgedStatus,
                _ => OpenStatus,
            };

        var severity = Severity switch
        {
            AlertSeverityChoice.Critical => "red",
            AlertSeverityChoice.Urgent => "orange",
            AlertSeverityChoice.Notice => "yellow",
            AlertSeverityChoice.Info => "green",
            _ => null,
        };

        DateTime? from = Window switch
        {
            AlertWindow.Today => today.Date,
            AlertWindow.Last7Days => today.Date.AddDays(-6),
            AlertWindow.Last30Days => today.Date.AddDays(-29),
            _ => null,
        };

        return (severity, status, from);
    }

    /// <summary>
    /// The parts narrowing the list, each with the words the strip and the header subtitle use, in
    /// the sheet's own order. Empty when nothing is narrowed.
    /// </summary>
    public IReadOnlyList<(AlertFilterPart Part, string Label)> Parts(bool archived)
    {
        var parts = new List<(AlertFilterPart, string)>(4);

        if (MemberId is not null)
            parts.Add((AlertFilterPart.Member, string.IsNullOrWhiteSpace(MemberName) ? UnnamedMemberLabel : MemberName));
        if (!archived && Status != AlertStatusChoice.AllOpen)
            parts.Add((AlertFilterPart.Status, StatusLabel(Status)));
        if (Severity != AlertSeverityChoice.Any)
            parts.Add((AlertFilterPart.Severity, SeverityLabel(Severity)));
        if (Window != AlertWindow.AnyTime)
            parts.Add((AlertFilterPart.Window, WindowLabel(Window)));

        return parts;
    }

    /// <summary>This filter with one part put back to its widest.</summary>
    public AlertListFilter Without(AlertFilterPart part) => part switch
    {
        AlertFilterPart.Member => this with { MemberId = null, MemberName = null },
        AlertFilterPart.Status => this with { Status = AlertStatusChoice.AllOpen },
        AlertFilterPart.Severity => this with { Severity = AlertSeverityChoice.Any },
        AlertFilterPart.Window => this with { Window = AlertWindow.AnyTime },
        _ => this,
    };

    public static string StatusLabel(AlertStatusChoice status) => status switch
    {
        AlertStatusChoice.NeedsResponse => "Needs response",
        AlertStatusChoice.Acknowledged => "Acknowledged",
        _ => "All open",
    };

    /// <summary>The badge words, in sentence case — the same four the alert cards wear.</summary>
    public static string SeverityLabel(AlertSeverityChoice severity) => severity switch
    {
        AlertSeverityChoice.Critical => "Critical",
        AlertSeverityChoice.Urgent => "Urgent",
        AlertSeverityChoice.Notice => "Notice",
        AlertSeverityChoice.Info => "Info",
        _ => "Any",
    };

    public static string WindowLabel(AlertWindow window) => window switch
    {
        AlertWindow.Today => "Today",
        AlertWindow.Last7Days => "Last 7 days",
        AlertWindow.Last30Days => "Last 30 days",
        _ => "Any time",
    };
}
