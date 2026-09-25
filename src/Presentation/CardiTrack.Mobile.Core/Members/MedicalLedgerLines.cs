using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// The words the Medical Information ledger is drawn with: its headings, the kinds a line can be,
/// and the caption under each line saying where it came from.
/// </summary>
/// <remarks>
/// Kept out of the page for the reason <see cref="RelativeTime"/> is: the branching — no author,
/// never confirmed, changed versus removed — is where the mistakes live, and the test project
/// cannot reach the MAUI assembly.
/// </remarks>
public static class MedicalLedgerLines
{
    /// <summary>The kinds, in the order the ledger groups them and the picker offers them.</summary>
    public static IReadOnlyList<MedicalEntryKind> Kinds { get; } =
        [MedicalEntryKind.Condition, MedicalEntryKind.Allergy, MedicalEntryKind.Medication, MedicalEntryKind.Other];

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

    /// <summary>What the text box suggests for each kind, so a caregiver writes one thing per line.</summary>
    public static string Placeholder(MedicalEntryKind kind) => kind switch
    {
        MedicalEntryKind.Condition => "e.g. Type 2 diabetes, diagnosed 2019",
        MedicalEntryKind.Allergy => "e.g. Penicillin — rash",
        MedicalEntryKind.Medication => "e.g. Metformin 500 mg, twice a day",
        _ => "Anything else a caregiver should know",
    };

    /// <summary>
    /// The current lines grouped under their headings, in <see cref="Kinds"/> order, empty groups
    /// left out. Within a group the server's order stands: oldest on file first.
    /// </summary>
    public static IReadOnlyList<(MedicalEntryKind Kind, IReadOnlyList<MedicalEntryResponse> Lines)> Group(
        IEnumerable<MedicalEntryResponse> current)
    {
        var byKind = current.ToLookup(e => Kinds.Contains(e.Kind) ? e.Kind : MedicalEntryKind.Other);
        return Kinds
            .Where(k => byKind[k].Any())
            .Select(k => (k, (IReadOnlyList<MedicalEntryResponse>)byKind[k].ToList()))
            .ToList();
    }

    /// <summary>
    /// Under a current line: "Added 12 Sep 2026 by Jane", then whether anybody has said it still
    /// holds since — "confirmed 3 months ago", or "not confirmed yet" for one nobody ever has.
    /// </summary>
    /// <remarks>
    /// No author reads as "On file since": that is a note carried over from before the ledger,
    /// which recorded neither who wrote it nor when, and "added by nobody" would be a claim about
    /// an author the app never knew. The confirmation is left out when it is the adding itself —
    /// a line is confirmed by whoever writes it, and saying so twice is noise.
    /// </remarks>
    public static string Caption(MedicalEntryResponse line, TimeZoneInfo? zone = null)
    {
        var added = DateOf(line.AddedAtUtc, zone);
        var origin = line.AddedByName is { Length: > 0 } by
            ? $"Added {added} by {by}"
            : $"On file since {added}";

        if (line.ConfirmedAtUtc is not { } confirmed)
            return $"{origin} · not confirmed yet";

        return confirmed - line.AddedAtUtc < TimeSpan.FromDays(1)
            ? origin
            : $"{origin} · confirmed {RelativeTime.Format(confirmed)}";
    }

    /// <summary>
    /// Under a line in the history: "Changed 12 Sep 2026 by Jane" for one an edit replaced,
    /// "Removed 12 Sep 2026" for one simply taken off — with the name when it is known.
    /// </summary>
    public static string HistoryCaption(MedicalEntryResponse line, TimeZoneInfo? zone = null)
    {
        var verb = line.WasChanged ? "Changed" : "Removed";
        var when = line.RemovedAtUtc is { } removed ? $" {DateOf(removed, zone)}" : string.Empty;
        var by = line.RemovedByName is { Length: > 0 } name ? $" by {name}" : string.Empty;
        return $"{verb}{when}{by}";
    }

    /// <summary>
    /// The list's own date line, for the whole ledger: when every line was last known to hold.
    /// Null <paramref name="reviewedAtUtc"/> says so plainly — see the review-date remarks on the
    /// server — rather than borrowing a date nobody confirmed anything on.
    /// </summary>
    public static string ReviewedLine(DateTime? reviewedAtUtc, TimeZoneInfo? zone = null) =>
        reviewedAtUtc is { } reviewed
            ? $"Every line confirmed since {DateOf(reviewed, zone)} · {RelativeTime.Format(reviewed)}"
            : "Not every line is confirmed yet — nobody has said whether some of this is still current.";

    private static string DateOf(DateTime utc, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local)
            .ToString("d MMM yyyy", CultureInfo.CurrentCulture);
}
