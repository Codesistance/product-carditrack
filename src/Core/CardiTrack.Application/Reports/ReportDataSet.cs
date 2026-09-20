using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Reports;

/// <summary>
/// Everything one export covers, gathered once and handed to whichever renderer the caregiver
/// asked for.
/// </summary>
/// <remarks>
/// A single gather, not one per renderer: the PDF's narrative and its tables must describe the
/// same days, and a second query against a live database could disagree with the first. It also
/// keeps the format choice where it belongs — in rendering, not in data access.
/// </remarks>
/// <param name="Members">One entry per requested CardiMember, in request order. Members the
/// caller could not read never reach here: access is vetted before the report is queued.</param>
/// <param name="From">The range the caregiver asked for — header, filename, daily
/// table, key figures, and the AI prompt. A pinned journal day does not move this.</param>
/// <param name="To">Inclusive end of <paramref name="From"/>.</param>
/// <param name="ChartFrom">The days the PDF figures plot. Same as <paramref name="From"/>
/// on a health-data export; the journal page's fortnight (or 30 days) when a book
/// entry is pinned.</param>
/// <param name="ChartTo">Inclusive end of <paramref name="ChartFrom"/>.</param>
public record ReportDataSet(
    IReadOnlyList<ReportMemberData> Members,
    DateOnly From,
    DateOnly To,
    string? Title,
    DateOnly ChartFrom,
    DateOnly ChartTo)
{
    /// <summary>
    /// Health-data exports — and tests that do not pin a journal — plot the
    /// same days they tabulate.
    /// </summary>
    public ReportDataSet(
        IReadOnlyList<ReportMemberData> Members,
        DateOnly From,
        DateOnly To,
        string? Title)
        : this(Members, From, To, Title, From, To)
    {
    }

    /// <summary>
    /// The member-chat conversation this export copies, or null on a health-data export.
    /// </summary>
    /// <remarks>
    /// An init-only property rather than another positional parameter: every existing caller
    /// builds a health-data set, and a transcript is not a further section of one but a different
    /// document that happens to travel the same queue, renderers and bucket. A renderer branches
    /// on it before it reads <see cref="ReportSections"/> at all.
    /// </remarks>
    public ChatTranscript? Transcript { get; init; }

    /// <summary>
    /// Readings that belong on the daily table and in the narrative — the
    /// requested days, not the wider chart window a pinned journal needs.
    /// </summary>
    public IReadOnlyList<ActivityLog> PeriodReadings(ReportMemberData member)
    {
        var logs = member.ActivityLogs;
        if (logs.Count == 0)
            return logs;

        // Gather orders by date. When the loaded span is already the request
        // range, skip the second pass — the common health-data case.
        if (ChartFrom == From && ChartTo == To)
            return logs;

        return logs.Where(l => l.Date >= From && l.Date <= To).ToList();
    }
}

/// <summary>One member's slice of an export.</summary>
/// <param name="Devices">Connections that produced the readings — the FHIR <c>Device</c>
/// resources, and the PDF's provenance line. Device labels are caregiver free text
/// (docs/technical/data_protection_architecture.md §70) and so are never rendered; only the
/// device type is.</param>
public record ReportMemberData(
    CardiMember Member,
    IReadOnlyList<ActivityLog> ActivityLogs,
    IReadOnlyList<Alert> Alerts,
    IReadOnlyList<DeviceConnection> Devices,
    IReadOnlyList<DigestEntry> Journals,
    IReadOnlyList<Notification> Notices)
{
    /// <summary>
    /// The member's established 30-day baseline, where they have one. Init-only rather than
    /// another positional parameter, following <see cref="ReportDataSet.Transcript"/>: every
    /// existing caller builds a set without it, and a document that cannot reach one is a document
    /// without a comparison section rather than a broken one.
    /// </summary>
    /// <remarks>
    /// Present so an export can say what a figure <em>means</em>. A column of resting heart rates
    /// with nothing beside it asks whoever reads the document — often the person least equipped to
    /// answer — whether 78 is high for this person.
    /// </remarks>
    public PatternBaseline? Baseline { get; init; }

    /// <summary>The stored reading against that baseline, where the pipeline has written one.</summary>
    public MemberInsight? BaselineInsight { get; init; }

    /// <summary>The stored trend narrative, where the member has a month of readings behind them.</summary>
    public MemberInsight? TrendInsight { get; init; }
}

/// <summary>
/// Which parts of the record the caregiver ticked on M1-17. Mirrors the request flags, so a
/// renderer never sees the transport DTO.
/// </summary>
public record ReportSections(
    bool IncludeMetrics,
    bool IncludeAlerts,
    bool IncludeDevices,
    bool IncludeTrends = true,
    bool IncludeJournals = false,
    bool IncludeNotices = false);

/// <summary>
/// A rendered export, ready to store and serve.
/// </summary>
/// <param name="Extension">Filename extension without the dot — also the object-name suffix in
/// the bucket, so an operator listing it can tell a PDF from a FHIR bundle.</param>
public record RenderedReport(byte[] Content, string ContentType, string Extension);
