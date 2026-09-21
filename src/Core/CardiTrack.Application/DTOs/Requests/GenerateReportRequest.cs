using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

public class GenerateReportRequest
{
    public required IReadOnlyList<Guid> CardiMemberIds { get; init; }
    public required DateOnly DateRangeFrom { get; init; }
    public required DateOnly DateRangeTo { get; init; }
    public required ReportFormat Format { get; init; }

    // FHIR-specific options (used when Format == FhirR4)
    public string FhirProfile { get; init; } = "us-core";
    public IReadOnlyList<string> FhirResources { get; init; } = ["Patient", "Observation", "Device"];

    // PDF/CSV section toggles
    public bool IncludeMetrics { get; init; } = true;
    public bool IncludeTrends { get; init; } = true;
    public bool IncludeAlerts { get; init; } = true;
    public bool IncludeDevices { get; init; } = false;
    public bool IncludeJournals { get; init; }
    public bool IncludeNotices { get; init; }

    /// <summary>
    /// When set, the export is the transcript of that member-chat conversation — what the
    /// caregiver asked and what the assistant answered, with the charts each reply carried —
    /// rather than the member's readings. Exactly one <see cref="CardiMemberIds"/> entry, which
    /// must be the member the conversation is about, and PDF or CSV only.
    /// </summary>
    /// <remarks>
    /// The section flags do not apply to it: a conversation has no alerts block and no daily
    /// table, and its charts are the ones stored on the replies rather than a window of readings
    /// re-plotted now. <see cref="DateRangeFrom"/> and <see cref="DateRangeTo"/> still frame the
    /// document — they are what the header, the filename and the consent fingerprint are built
    /// from — so a client sends the days the conversation actually spans.
    /// </remarks>
    public Guid? ChatSessionId { get; init; }

    /// <summary>
    /// When set with <see cref="JournalAudience"/>, the export is scoped to that
    /// one journal entry rather than every book in the date range.
    /// </summary>
    public DateOnly? JournalEntryDate { get; init; }

    public DigestAudience? JournalAudience { get; init; }

    /// <summary>
    /// Single-use token from <c>POST /api/v1/reports/consent</c>. Required — an
    /// export without a recorded responsibility confirmation is refused.
    /// </summary>
    public string? ConsentToken { get; init; }

    public string? Title { get; init; }
}
