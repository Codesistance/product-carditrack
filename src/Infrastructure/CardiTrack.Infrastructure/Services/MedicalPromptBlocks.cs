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
    /// The voice every member-facing generation speaks in. Leads every instruction block, so what
    /// a caregiver reads sounds like one product whichever path produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately two-sided. "Be reassuring" on its own would be an unsafe instruction to give
    /// the prompts behind an alerting service — a model told only to soothe will soften the one
    /// reading that needed saying plainly. So the rule is that the words must not distort the
    /// readings in <em>either</em> direction: no urgency the data does not carry, and no
    /// reassurance it does not support. Calm is the default because most days are calm, not
    /// because calm is always the answer.
    /// </para>
    /// <para>
    /// A <c>const</c>, and first in every prompt, because these blocks are the cacheable fixed
    /// prefix the serving engine reuses between calls (docs/llm_design.md). Composed at compile
    /// time, so prepending it costs nothing and cannot vary per member.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Line breaks are load-bearing, not cosmetic: a caller's echo guard matches phrases against
    /// the model's reply, and a phrase split across two lines here could never be matched whole.
    /// Each rule therefore sits on one line. See <c>DigestGenerationService.InstructionEchoes</c>.
    /// </remarks>
    internal const string Tone = """
        Tone: you are writing for a worried family member, not for a clinician.
        Be plain, warm and steady, and write as one person telling another how someone is doing.
        Say what the readings show without dressing it up and without sharpening it.
        Add no urgency the data does not carry, and no reassurance it does not support either.
        Where a plain phrase says as much as a figure, prefer the phrase.
        Never suggest the family has missed something or done something wrong.
        Never diagnose.

        """;

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
    /// The trailing daily readings, oldest first, each opening with which day it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ingestion stores the day in progress, so the newest line is a part-finished day whose totals
    /// are not comparable with the completed days above it. It is labelled rather than dropped: the
    /// model is being asked to explain deviations, and an unmarked partial day reads as a collapse
    /// in activity that the member is not actually having.
    /// </para>
    /// <para>
    /// The label leads the line, and says "Yesterday" rather than only a date. It used to trail it
    /// as a parenthetical after an ISO date, and a family summary came back attributing yesterday's
    /// step total to today while taking that same sentence's sleep figure from the correct row —
    /// two rows of identical shape, told apart only by a date the model had to relate to a "today"
    /// nobody had named, with the one disambiguating note arriving after the numbers it governed.
    /// A reader who has to backtrack to find out which day they just read is a reader who
    /// sometimes will not. The dates stay, in parentheses, because they still carry the weekday
    /// pattern a week-long window is read for.
    /// </para>
    /// </remarks>
    internal static string DailyLines(IEnumerable<ActivityLog> logs, int take, DateOnly today)
    {
        var lines = logs
            .TakeLast(take)
            .Select(l =>
                $"  {DayLabel(l.Date, today)}: "
                + $"steps={l.Steps}, HR={l.RestingHeartRate}, sleep={l.SleepMinutes}min")
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : "No recent activity data.";
    }

    /// <summary>
    /// Which day a reading belongs to, said before the reading rather than after it. Relative to
    /// the member's own today, because that is the anchor the model is missing — it cannot know
    /// what today's date is except by being told.
    /// </summary>
    private static string DayLabel(DateOnly date, DateOnly today) =>
        (today.DayNumber - date.DayNumber) switch
        {
            <= 0 => $"Today so far ({date}, still in progress — totals are partial)",
            1 => $"Yesterday ({date}, complete day)",
            var days => $"{days} days ago ({date}, complete day)",
        };
}
