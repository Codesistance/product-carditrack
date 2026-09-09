using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;

namespace CardiTrack.API.Validators;

/// <summary>
/// Shared journal-scope predicates for generate and consent. A consent that would
/// mint a token for the live Family glance must fail the same way generate would.
/// </summary>
internal static class ExportJournalRules
{
    public const string FinishedBooksOnly =
        "Journals export a Daybook, Weekbook or Monthbook — not the live glance";

    public const string DayInRange =
        "The journal day must fall inside the date range you are exporting";

    public const string ScopeNeedsJournals =
        "A journal day or book is only used when journals are included";

    public static bool AudienceIsAllowed(DigestAudience? audience) =>
        audience is null || ReportJournalScope.IsFinishedBook(audience.Value);

    public static bool ScopeMatchesJournalsFlag(
        bool includeJournals, DigestAudience? audience, DateOnly? day) =>
        includeJournals || (audience is null && day is null);
}
