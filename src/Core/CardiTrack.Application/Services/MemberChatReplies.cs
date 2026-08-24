using System.Globalization;
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
public static class MemberChatReplies
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

        var latest = recent
            .Where(l => l.Steps is not null || l.RestingHeartRate is not null || l.SleepMinutes is not null)
            .OrderBy(l => l.Date)
            .LastOrDefault();

        if (latest is null)
            return opening + " I don't have any recent readings for them either.";

        // Cased for the middle of a sentence as each piece needs: the two relative words are
        // lowercase there, a month name is not. Lowercasing the lot turned "Aug 19" into
        // "aug 19", which reads as a typo and disagrees with every date the UI draws.
        var when = latest.Date == today
            ? "today so far"
            : latest.Date == today.AddDays(-1)
                ? "yesterday"
                : latest.Date.ToString("MMM d", CultureInfo.InvariantCulture);

        var parts = new List<string>();
        if (latest.Steps is { } steps)
            parts.Add($"{steps:#,##0} steps");
        if (latest.RestingHeartRate is { } hr)
            parts.Add($"a resting heart rate of {hr} bpm");
        if (latest.SleepMinutes is { } sleep)
            parts.Add($"{ReadingFigures.SleepFigure(sleep)} of sleep the night before");

        return $"{opening} The most recent I have is {when}: {Join(parts)}.";
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
    /// Closes by marking what it just said as a suggestion, every time. A suggestion arriving in
    /// the same voice that answered "how did he sleep" a moment earlier is the one place on this
    /// platform where a caregiver could most easily read guidance as an instruction, and the card
    /// on CardiMember Details has a heading and a layout to carry that framing where a chat bubble
    /// has neither.
    /// </para>
    /// <para>
    /// It does not name the reference the suggestion drew on, though it still refuses to serve a
    /// row that has none. Read aloud, "that's general wellness guidance based on Adult physical
    /// activity" is a citation, and a citation is what made the first version of this reply sound
    /// like a leaflet rather than an answer — the second half of the same problem that had the
    /// model itself saying "it's a general wellness thing" (see
    /// <c>MedicalPromptBlocks.ToneWellnessNotClinical</c>'s remark). The grounding is a
    /// generation-time gate, not something the caregiver has to be shown to be safe; the Details
    /// card still sets it as "Based on: …" for anyone who wants it.
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

        return $"{advise.Summary.Trim()} {advise.Suggestion.Trim()} That's just an idea to "
            + "consider — their doctor is the one to ask if you're unsure about it.";
    }
}
