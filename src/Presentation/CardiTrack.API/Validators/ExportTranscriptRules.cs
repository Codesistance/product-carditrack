using CardiTrack.Domain.Enums;

namespace CardiTrack.API.Validators;

/// <summary>
/// Shared chat-transcript predicates for generate and consent. A confirmation that would mint a
/// token for an export generate refuses must fail the same way generate does.
/// </summary>
internal static class ExportTranscriptRules
{
    public const string OneMember =
        "A conversation export covers the one person the conversation is about";

    public const string PdfOrCsv =
        "Conversations export as PDF or CSV — a FHIR bundle has nowhere to put a conversation";

    public const string NotAlsoJournals =
        "Export a conversation or a journal, not both in one file";

    /// <summary>
    /// One member, and the one the conversation is about — which the pipeline checks against the
    /// session itself. Here it is only the count: a transcript with five members named would
    /// have four of them in the consent fingerprint and in none of the file.
    /// </summary>
    public static bool NamesOneMember(Guid? chatSessionId, IReadOnlyList<Guid>? memberIds) =>
        chatSessionId is null || memberIds is { Count: 1 };

    /// <summary>
    /// FHIR R4 has no resource for "a caregiver asked this and was told that" — a bundle built
    /// from a transcript would be a Patient resource and nothing else, which is a successful
    /// export missing the only thing that was asked for.
    /// </summary>
    public static bool FormatCarriesAConversation(Guid? chatSessionId, ReportFormat format) =>
        chatSessionId is null || format is ReportFormat.Pdf or ReportFormat.Csv;

    /// <summary>
    /// The two scopes are alternatives, not layers: one export is one document, and a file that
    /// was both a conversation and a month of Daybooks would have no title that described it.
    /// </summary>
    public static bool NotAlsoAJournalExport(
        Guid? chatSessionId, bool includeJournals, DigestAudience? audience, DateOnly? entryDate) =>
        chatSessionId is null || (!includeJournals && audience is null && entryDate is null);

    /// <summary>
    /// Whether the section flags have to name something renderable. They do not on a transcript:
    /// its content is the conversation, and the flags — which default on — say nothing about it.
    /// </summary>
    public static bool SectionsMustBeChosen(Guid? chatSessionId) => chatSessionId is null;
}
