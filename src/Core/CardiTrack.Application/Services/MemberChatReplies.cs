using System.Globalization;
using System.Text.RegularExpressions;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Member chat's code-assembled replies — the ladder's two zero-model-call rungs
/// (docs/technical/member_chat_routing.md §5), written in code precisely so no model can assemble
/// a different sentence. Beside <see cref="AdvisePicker"/>, <see cref="AdviseServability"/> and
/// <see cref="AlertDetailComposer"/> because they are the same kind of thing: reply-composition
/// policy with no I/O, testable without a host. <c>MemberChatService</c> fetches the rows and
/// resolves the first name; everything after that is a pure function of what it passes in.
/// </summary>
public static partial class MemberChatReplies
{
    /// <summary>
    /// The answer to "is he asleep now?" — the limit first, then the most recent thing actually
    /// recorded, so the answer is useful rather than only honest.
    /// </summary>
    /// <remarks>
    /// Leads with what cannot be seen because that is the part the caregiver has to know — a
    /// reading offered first would be read as the answer to the question they asked. Names the day
    /// a figure belongs to for the same reason: "4,200 steps" with no date invites exactly the
    /// present-tense reading this whole path exists to prevent.
    /// </remarks>
    public static string LiveStatusReply(string? firstName, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        // "what they're doing" rather than a stand-in noun when there is no name: every
        // relationship word here would be invented, and "what them is doing" is what a bare
        // substitution produces.
        var subject = string.IsNullOrWhiteSpace(firstName) ? "they're" : $"{firstName} is";
        var opening =
            $"I can't see what {subject} doing right now — readings only reach me after their watch "
            + "has recorded and synced them, so there's nothing live here to check.";

        return LatestFigures(recent, today) is not { } latest
            ? opening + " I don't have any recent readings for them either."
            : $"{opening} The most recent I have is {latest.When}: {latest.Figures}.";
    }

    /// <summary>
    /// The readings on file, for a caregiver who asked how someone is rather than what they are
    /// doing this instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same figures <see cref="LiveStatusReply"/> states, without the liveness disclaimer in
    /// front of them. That disclaimer is the right opening for "is he asleep now?" and the wrong
    /// one for "how is he today": it answers a question the caregiver did not ask, and spends the
    /// first forty words of the reply doing it. Which of the two runs is decided by the triage
    /// call's <c>isAboutThisMoment</c>, whose own prompt draws exactly this line — "a question
    /// about a period, however recent, is not this".
    /// </para>
    /// <para>
    /// Serves as the fallback when there is no current status line, rather than declining: §5's
    /// rule for this rung is that past the staleness ceiling it computes from readings, because
    /// unlike a suggestion there is always something to say.
    /// </para>
    /// </remarks>
    public static string LatestReadingsReply(
        string? firstName, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        var subject = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

        return LatestFigures(recent, today) is not { } latest
            ? $"I don't have any recent readings for {subject} yet — they arrive once their watch "
              + "has recorded and synced them."
            : $"The most recent readings I have for {subject} are {latest.When}: {latest.Figures}.";
    }

    /// <summary>
    /// The newest day with anything recorded on it, as a dated figure list — shared by the two
    /// status replies so they cannot state the same readings differently.
    /// </summary>
    private static (string When, string Figures)? LatestFigures(
        IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        var latest = recent
            .Where(l => l.Steps is not null || l.RestingHeartRate is not null || l.SleepMinutes is not null)
            .OrderBy(l => l.Date)
            .LastOrDefault();

        if (latest is null)
            return null;

        var parts = new List<string>();
        if (latest.Steps is { } steps)
            parts.Add($"{steps:#,##0} steps");
        if (latest.RestingHeartRate is { } hr)
            parts.Add($"a resting heart rate of {hr} bpm");
        if (latest.SleepMinutes is { } sleep)
            parts.Add($"{ReadingFigures.SleepFigure(sleep)} of sleep the night before");

        return (DayLabel(latest.Date, today), Join(parts));
    }

    /// <summary>
    /// The one way this app spells the day a figure belongs to.
    /// </summary>
    /// <remarks>
    /// Cased for the middle of a sentence as each piece needs: the two relative words are
    /// lowercase there, a month name is not. Lowercasing the lot turned "Aug 19" into "aug 19",
    /// which reads as a typo and disagrees with every date the UI draws.
    /// <para>
    /// Public because the generated rungs need the same spelling. <see cref="LiveStatusReply"/>
    /// dated every figure it stated from the day this path was written; the analysis and inference
    /// replies did not, and the two rungs answering the same question minutes apart with figures
    /// from different days — neither named — is what this is shared to prevent.
    /// </para>
    /// </remarks>
    public static string DayLabel(DateOnly date, DateOnly today) => date == today
        ? "today so far"
        : date == today.AddDays(-1)
            ? "yesterday"
            : date.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>The stretch a set of figures covers, in <see cref="DayLabel"/>'s vocabulary.</summary>
    /// <remarks>
    /// The far end keeps its relative word, so a span ending today still says "today so far" and
    /// carries the partial-day warning with it — "Aug 30 to today so far" is the honest reading of
    /// a window whose last day has not finished.
    /// </remarks>
    public static string SpanLabel(DateOnly from, DateOnly to, DateOnly today) => from == to
        ? DayLabel(from, today)
        : $"{from.ToString("MMM d", CultureInfo.InvariantCulture)} to {DayLabel(to, today)}";

    /// <summary>
    /// The span a clinical read says its figures came from, or null when that cannot be trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same closed-vocabulary discipline as every other parse of model output on this
    /// platform: the model picks <em>which</em> dates, this decides whether they are usable, and
    /// <see cref="DayAttribution"/> writes the words a caregiver reads. An unparseable date, or one
    /// outside the window that was actually fetched, is dropped rather than coerced — exactly as
    /// <c>ChatDataRegistry.CitationsFor</c> drops an authority it does not carry. Nothing claimed
    /// beats something invented, and a wrong date is worse than no date.
    /// </para>
    /// <para>
    /// Strict <c>yyyy-MM-dd</c>, not a lenient parse: a loose one accepts "Sep 4" and resolves the
    /// year from the current culture, which is how a reply ends up dated to a year with no
    /// readings in it.
    /// </para>
    /// </remarks>
    public static (DateOnly From, DateOnly To)? ResolveSpan(
        string? from, string? to, (DateOnly From, DateOnly To)? fetchedWindow)
    {
        // No activity fetched means no figures to date — an answer from member context and the
        // baseline alone has no day to name, and naming one would invent it.
        if (fetchedWindow is not { } window)
            return null;

        if (!DateOnly.TryParseExact(from?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
            return null;

        // A model that returns the pair the wrong way round has still told us which two days it
        // meant; the order is presentation, and correcting it costs nothing.
        if (end < start)
            (start, end) = (end, start);

        return start < window.From || end > window.To ? null : (start, end);
    }

    /// <summary>
    /// A reply with the day its figures belong to stated in code, when the reply has not already
    /// said so itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended rather than woven in, for the reason <c>AdviseReply</c> quotes its authority at the
    /// end: a sentence assembled here cannot be a sentence the rewrite model composed, and the
    /// rewrite model is the step that dropped the day in the first place. The clinical prompt
    /// already labels every row it is given ("Today so far (…partial)", "Yesterday (…complete
    /// day)"); it is the rewrite that turned "yesterday, complete" into "a stable day".
    /// </para>
    /// <para>
    /// Suppressed when the reply already names the day — the same conditional-append shape
    /// <c>AdviseReply</c> uses for its doctor line, and for the same reason: one statement is the
    /// framing, two is a stutter. The marker is derived from the <em>validated window</em>, never
    /// from the reply, so a reply that says "today" about yesterday's figures is corrected rather
    /// than left alone.
    /// </para>
    /// </remarks>
    public static string WithDayAttribution(
        string reply, DateOnly from, DateOnly to, DateOnly today)
    {
        // The bare day word, because a reply saying "yesterday" has dated itself even though it
        // did not spell the label's "today so far" in full.
        var marker = from == to
            ? from == today ? "today"
                : from == today.AddDays(-1) ? "yesterday"
                : from.ToString("MMM d", CultureInfo.InvariantCulture)
            : SpanLabel(from, to, today);

        if (reply.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return reply;

        var label = SpanLabel(from, to, today);
        var sentence = from == to
            ? $"Those figures are for {label}."
            : $"Those figures cover {label}.";

        return $"{reply}\n\n{sentence}";
    }

    /// <summary>Oxford-less list joining — "a, b and c".</summary>
    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "nothing recorded",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>
    /// The stored suggestion as one caregiver-facing reply, or an honest "nothing right now" when
    /// there is no current row to serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opens with the suggestion, not the summary. The question this rung answers is "what could
    /// I do?", and a reply that led with three sentences of readings before getting to the one
    /// actionable sentence read as not answering it — the summary still travels, as the grounding
    /// after the answer rather than a preamble before it.
    /// </para>
    /// <para>
    /// Closes by marking what it just said as a suggestion — but only when the stored suggestion
    /// has not already done so itself. The generation prompt's <c>ToneWellnessNotClinical</c> asks
    /// for "worth mentioning to their doctor", so most rows arrive with a doctor line of their
    /// own, and appending this one unconditionally told the caregiver to see the doctor twice in
    /// three sentences. One line is the framing; two is a nag. A suggestion arriving in
    /// the same voice that answered "how did he sleep" a moment earlier is the one place on this
    /// platform where a caregiver could most easily read guidance as an instruction, and the card
    /// on CardiMember Details has a heading and a layout to carry that framing where a chat bubble
    /// has neither.
    /// </para>
    /// <para>
    /// The authority behind the suggestion is quoted at the end, as a References line — the same
    /// convention the inference rung closes with (decision 2026-08-24, reversing the earlier
    /// no-citation-in-chat choice). Not woven into the prose: "based on Adult physical activity"
    /// mid-sentence is what made an early version read like a leaflet. The quoted text is
    /// <see cref="WellnessGuidelines"/>' fixed lines, mapped from the stored
    /// <see cref="MemberAdvise.GuidelineCited"/> — the model picked which reference at generation
    /// time; code decides the words a caregiver reads, and a pick the closed set does not carry
    /// quotes nothing rather than something invented.
    /// </para>
    /// <para>
    /// A row with no <see cref="MemberAdvise.GuidelineCited"/> is still treated as nothing to serve
    /// rather than served bare — the same call <c>AdviseGenerationService</c> makes when it
    /// withholds such a row, and what <see cref="DTOs.Responses.AdviseResponse.GuidelineCited"/>
    /// tells clients to do with a null. That rule lives in <see cref="AdviseServability"/> rather
    /// than here: stated only in this method it made chat disagree with the Details card and the
    /// Dashboard pulse dot, which went on rendering such a row and lighting for it.
    /// </para>
    /// <para>
    /// The empty case says why there is nothing and what can be asked instead, rather than only
    /// declining: an advice question is one a caregiver asks when they are worried, and "no" on its
    /// own is the least useful moment to be terse with them.
    /// </para>
    /// </remarks>
    public static string AdviseReply(string? firstName, MemberAdvise? advise, DateTime utcNow)
    {
        if (!AdviseServability.IsServable(advise, utcNow))
        {
            // "them" rather than an invented relationship word, for the reason LiveStatusReply's
            // subject line gives at length.
            var subject = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;
            return $"I don't have a suggestion for {subject} right now — those come from their "
                + "readings once a day, and there isn't a current one. I can tell you how their "
                + "sleep, activity or heart rate compare with what's usual for them, though.";
        }

        var reply = $"{advise.Suggestion.Trim()} {advise.Summary.Trim()}";

        if (!DoctorMention().IsMatch(reply))
        {
            reply += " That's just an idea to consider — their doctor is the one to ask if "
                + "you're unsure about it.";
        }

        return WellnessGuidelines.CitationFor(advise.GuidelineCited) is { } citation
            ? $"{reply}\n\nReference: {citation}."
            : reply;
    }

    /// <summary>The stored row already routes to a clinician, in whichever word the model chose —
    /// what makes the appended framing line redundant rather than required.</summary>
    [GeneratedRegex(@"\b(?:doctors?|GPs?|physicians?|clinicians?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorMention();
}
