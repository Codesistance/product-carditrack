using System.Globalization;
using System.Text.RegularExpressions;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Core.Members;

/// <summary>How the Medical Information list as a whole stands, for the chip at its head.</summary>
public enum LedgerReviewTone
{
    /// <summary>Every line confirmed within the review interval.</summary>
    Current,

    /// <summary>Confirmed once, but longer ago than the review interval.</summary>
    Due,

    /// <summary>At least one line nobody has ever confirmed.</summary>
    Unconfirmed,
}

/// <summary>
/// The words the Medical Information ledger is drawn with: its headings, the kinds a line can be,
/// the caption under each line, the review chip, and how a block of old notes is cut into lines.
/// </summary>
/// <remarks>
/// Kept out of the page for the reason <see cref="RelativeTime"/> is: the branching — no author,
/// never confirmed, changed versus removed, due or not — is where the mistakes live, and the test
/// project cannot reach the MAUI assembly.
/// </remarks>
public static partial class MedicalLedgerLines
{
    /// <summary>
    /// How long a confirmation holds before the list is due a check — the same six months the
    /// server's out-of-date reminder waits (<c>MedicalNotesStaleRule.ReviewInterval</c>), so the
    /// chip turns amber on the day the reminder would first be sent, not before or after it.
    /// </summary>
    public static readonly TimeSpan ReviewInterval = TimeSpan.FromDays(183);

    /// <summary>The kinds in the order the picker offers them.</summary>
    public static IReadOnlyList<MedicalEntryKind> Kinds { get; } =
        [MedicalEntryKind.Condition, MedicalEntryKind.Allergy, MedicalEntryKind.Medication, MedicalEntryKind.Other];

    /// <summary>
    /// The order the list shows them: what somebody arriving in a hurry needs first. An allergy
    /// and a medicine are the two questions a paramedic asks before anything else, so they lead,
    /// and a long-standing condition follows.
    /// </summary>
    public static IReadOnlyList<MedicalEntryKind> DisplayOrder { get; } =
        [MedicalEntryKind.Allergy, MedicalEntryKind.Medication, MedicalEntryKind.Condition, MedicalEntryKind.Other];

    /// <summary>One line's kind, as the picker names it.</summary>
    public static string KindName(MedicalEntryKind kind) => kind switch
    {
        MedicalEntryKind.Condition => "Condition",
        MedicalEntryKind.Allergy => "Allergy",
        MedicalEntryKind.Medication => "Medication",
        _ => "Other",
    };

    /// <summary>A group's heading on the ledger.</summary>
    public static string Heading(MedicalEntryKind kind) => kind switch
    {
        MedicalEntryKind.Condition => "Conditions",
        MedicalEntryKind.Allergy => "Allergies",
        MedicalEntryKind.Medication => "Medications",
        _ => "Other notes",
    };

    /// <summary>A group's icon, drawn beside its heading.</summary>
    public static string HeadingIcon(MedicalEntryKind kind) => kind switch
    {
        MedicalEntryKind.Allergy => "icon_ledger_allergy.svg",
        MedicalEntryKind.Medication => "icon_ledger_medication.svg",
        MedicalEntryKind.Condition => "icon_ledger_condition.svg",
        _ => "icon_ledger_other.svg",
    };

    /// <summary>What the text box suggests for each kind, so a caregiver writes one thing per line.</summary>
    public static string Placeholder(MedicalEntryKind kind) => kind switch
    {
        MedicalEntryKind.Condition => "e.g. Type 2 diabetes, diagnosed 2019",
        MedicalEntryKind.Allergy => "e.g. Penicillin — rash",
        MedicalEntryKind.Medication => "e.g. Metformin 500 mg, twice a day",
        _ => "Anything else a caregiver should know",
    };

    /// <summary>
    /// The current lines grouped under their headings, in <see cref="DisplayOrder"/>, empty groups
    /// left out. Within a group the server's order stands: oldest on file first.
    /// </summary>
    public static IReadOnlyList<(MedicalEntryKind Kind, IReadOnlyList<MedicalEntryResponse> Lines)> Group(
        IEnumerable<MedicalEntryResponse> current)
    {
        var byKind = current.ToLookup(e => DisplayOrder.Contains(e.Kind) ? e.Kind : MedicalEntryKind.Other);
        return DisplayOrder
            .Where(k => byKind[k].Any())
            .Select(k => (k, (IReadOnlyList<MedicalEntryResponse>)byKind[k].ToList()))
            .ToList();
    }

    /// <summary>
    /// Under a current line, once: who put it on file and when — "Added by Jane · 12 Sep 2026" —
    /// then, if somebody has since said it still holds, how long ago that was.
    /// </summary>
    /// <remarks>
    /// One date, in the caption, rather than a date column beside it saying the same thing again.
    /// No author reads as "On file since": either a note carried over from before the ledger, which
    /// recorded no author, or a caregiver whose account has since been closed — and the caption
    /// must be true of both. The confirmation is left out while it is the adding itself — a line is
    /// confirmed by whoever writes it, and saying so twice is noise. A line nobody ever confirmed is
    /// what the chip at the head of the list is for, so it is not repeated on the line.
    /// </remarks>
    public static string Caption(MedicalEntryResponse line, TimeZoneInfo? zone = null)
    {
        var added = DateOf(line.AddedAtUtc, zone);
        var origin = line.AddedByName is { Length: > 0 } by
            ? $"Added by {by} · {added}"
            : $"On file since {added}";

        return line.ConfirmedAtUtc is { } confirmed && confirmed - line.AddedAtUtc >= TimeSpan.FromDays(1)
            ? $"{origin} · confirmed {RelativeTime.Format(confirmed)}"
            : origin;
    }

    /// <summary>
    /// Under a line in the history, in the same shape as a current line's: "Changed by Tom ·
    /// 15 Sep 2026" for one an edit replaced, "Removed · 15 Sep 2026" when nobody is named.
    /// </summary>
    public static string HistoryCaption(MedicalEntryResponse line, TimeZoneInfo? zone = null)
    {
        var verb = line.WasChanged ? "Changed" : "Removed";
        var by = line.RemovedByName is { Length: > 0 } name ? $" by {name}" : string.Empty;
        var when = line.RemovedAtUtc is { } removed ? $" · {DateOf(removed, zone)}" : string.Empty;
        return $"{verb}{by}{when}";
    }

    /// <summary>
    /// The chip at the head of the list: whether it is current, due a check, or holds something
    /// nobody has confirmed — and in words, how long since. Null for an empty list, which has
    /// nothing to be current about.
    /// </summary>
    public static (LedgerReviewTone Tone, string Text)? ReviewStatus(
        int currentLines, DateTime? reviewedAtUtc, DateTime utcNow)
    {
        if (currentLines == 0)
            return null;

        if (reviewedAtUtc is not { } reviewed)
            return (LedgerReviewTone.Unconfirmed, "Not confirmed yet");

        return utcNow - reviewed >= ReviewInterval
            ? (LedgerReviewTone.Due, $"Last checked {RelativeTime.Format(reviewed)}")
            : (LedgerReviewTone.Current, $"Confirmed {RelativeTime.Format(reviewed)}");
    }

    /// <summary>
    /// A line that looks like the old notes written as one block: nobody's name on it, filed as
    /// Other, and more than one statement in it. What the "Sort into lines" prompt is offered for.
    /// </summary>
    /// <remarks>
    /// Asked of what the line is rather than where it came from, because the list does not say:
    /// a carried-over note and a line whose author has left look alike. Both are worth sorting when
    /// they hold several things, and neither is when they hold one.
    /// </remarks>
    public static bool IsUnsortedBlock(MedicalEntryResponse line) =>
        line.Kind == MedicalEntryKind.Other
        && line.AddedByName is null
        && SplitIntoStatements(line.Text).Count > 1;

    /// <summary>
    /// A block of notes cut at its sentence and line breaks, each piece trimmed and without its
    /// closing full stop — "Type 2 diabetes. Penicillin allergy." is "Type 2 diabetes" and
    /// "Penicillin allergy". A decimal point ("0.5 mg") or an abbreviation ("approx. 2") followed
    /// by a digit or a lower-case word is not a break.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoStatements(string text) =>
        StatementBreak().Split(text)
            .Select(piece => piece.Trim().TrimEnd('.', ';').Trim())
            .Where(piece => piece.Length > 0)
            .ToList();

    // A newline, or a full stop / semicolon / ! / ? followed by whitespace and a capital letter —
    // the start of a new statement. A digit or a lower-case word after the stop is not one, which
    // is what keeps "0.5 mg" and "approx. 2 a day" whole. The lookbehind keeps the stop on the
    // piece so the trim above decides what to drop.
    [GeneratedRegex(@"(?:\r?\n)+|(?<=[.;!?])\s+(?=[A-Z])")]
    private static partial Regex StatementBreak();

    private static string DateOf(DateTime utc, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local)
            .ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
