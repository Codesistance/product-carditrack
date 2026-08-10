using System.Text.RegularExpressions;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The prompt fragments every private-model caller shares — extracted from
/// <see cref="HealthInsightService"/> when the digest pipeline became the second writer of
/// member-facing prompts, so the minimisation and injection-framing rules cannot drift between
/// callers.
/// </summary>
internal static partial class MedicalPromptBlocks
{
    /// <summary>
    /// Caregiver notes are unbounded free text. A long note would crowd the metrics out of the
    /// context window and cost inference time on a single CPU-served model, so it is truncated
    /// visibly rather than silently.
    /// </summary>
    internal const int MaxNoteLength = 1_000;

    /// <summary>
    /// Who the member is, as far as the model needs to know: age and sex change how a heart rate
    /// or a sleep duration should be read, and caregiver notes carry conditions and medication the
    /// wearable cannot see. Name and id are deliberately absent — they would identify the member
    /// to the model without changing a word of the clinical interpretation.
    /// </summary>
    internal static string MemberContext(CardiMember? member, DateOnly today)
    {
        if (member is null)
            return "No member profile available.";

        var lines = new List<string> { $"Age: {member.DateOfBirth.ToAgeInYears(today)}" };

        // Only the two values that carry a clinical reading are passed on; "Other" and
        // "Prefer not to say" tell the model nothing it can use.
        if (member.Gender is Gender.Male or Gender.Female)
            lines.Add($"Sex: {member.Gender}");

        if (!string.IsNullOrWhiteSpace(member.MedicalNotes))
            lines.Add($"Caregiver-reported context: {Flatten(member.MedicalNotes)}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reduces a caregiver note to a single line, then truncates.
    /// <para>
    /// The instruction blocks scope their warning to what sits under "Caregiver-reported context",
    /// and the note is the last line of the member block — so a newline inside it would put the rest
    /// of the note on an unlabelled top-level line, or let it forge a section delimiter of its own.
    /// Collapsing whitespace makes the note structurally unable to leave the line it was put on;
    /// the framing then covers all of it, which is what makes the framing worth anything.
    /// </para>
    /// Truncation comes after flattening so the cap applies to what is actually sent.
    /// </summary>
    internal static string Flatten(string note)
    {
        var flattened = WhitespaceRuns().Replace(note, " ").Trim();
        return flattened.Length > MaxNoteLength
            ? $"{flattened[..MaxNoteLength]}… (truncated)"
            : flattened;
    }

    /// <summary>Any run of whitespace or control characters, including newlines.</summary>
    [GeneratedRegex(@"[\s\p{Cc}]+")]
    private static partial Regex WhitespaceRuns();

    /// <summary>
    /// The trailing daily readings, oldest first.
    /// </summary>
    /// <remarks>
    /// Ingestion stores the day in progress, so the newest line is a part-finished day whose totals
    /// are not comparable with the completed days above it. It is labelled rather than dropped: the
    /// model is being asked to explain deviations, and an unmarked partial day reads as a collapse
    /// in activity that the member is not actually having.
    /// </remarks>
    internal static string DailyLines(IEnumerable<ActivityLog> logs, int take, DateOnly today)
    {
        var lines = logs
            .TakeLast(take)
            .Select(l => $"  {l.Date}: steps={l.Steps}, HR={l.RestingHeartRate}, sleep={l.SleepMinutes}min"
                         + (l.Date == today ? "  (today, still in progress — totals are partial)" : string.Empty))
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : "No recent activity data.";
    }
}
