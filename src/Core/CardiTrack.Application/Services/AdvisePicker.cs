using System.Text.RegularExpressions;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Which of a member's topic-scoped suggestions answers a given question — the selection policy
/// chat, and any future topic-aware reader, share. Pure and in Application beside
/// <see cref="AdviseServability"/>, because it is the same kind of thing: reply-selection policy
/// with no I/O.
/// </summary>
/// <remarks>
/// The topic comes from the question's own words, decided in code rather than asked of a model —
/// the router already spent the one classification this turn gets, and a keyword miss falls back
/// rather than failing: named topic first, then the general row, then the most recent servable
/// anything. A caregiver asking about sleep with only an activity suggestion on file gets the
/// activity suggestion rather than nothing, because <c>AdviseReply</c>'s empty case only honestly
/// applies when there is nothing at all to serve.
/// </remarks>
public static partial class AdvisePicker
{
    /// <summary>The topic a question names, or null when it names none.</summary>
    /// <remarks>
    /// Whole words, not substrings, and the heart terms are checked first. Both halves came from
    /// the same failure: "should I worry about his resting heart rate?" contains the substring
    /// "rest", and with sleep checked first it served the sleep suggestion to a heart-rate
    /// question. Word boundaries stop "rest" firing inside "resting" or "interested", and
    /// heart/pulse/bpm/cardiac go first because they are the unambiguous clinical words — a
    /// question that names the heart is about the heart whatever else it mentions.
    /// </remarks>
    public static AdviseTopic? TopicOf(string question)
    {
        if (HeartWords().IsMatch(question))
            return AdviseTopic.HeartRate;
        if (SleepWords().IsMatch(question))
            return AdviseTopic.Sleep;
        if (ActivityWords().IsMatch(question))
            return AdviseTopic.Activity;

        return null;
    }

    [GeneratedRegex(@"\b(?:heart|pulse|bpm|cardiac)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeartWords();

    [GeneratedRegex(@"\b(?:sleep\w*|slept|nights?|naps?|rest|rested|resting|bed\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SleepWords();

    [GeneratedRegex(@"\b(?:walk\w*|steps?|activity|active|exercis\w*|moving|moves?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActivityWords();

    /// <summary>
    /// True when the question asks <em>which</em>, <em>how much</em> or <em>how often</em> rather
    /// than <em>whether</em> — the shapes a standing, pre-generated suggestion cannot answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The topic match is deliberately coarse: "what kind of exercises can he do" contains
    /// "exercis", so it picks the activity row and serves it verbatim — "add more movement, like
    /// short walks during breaks". Every one of "what kind", "how much", "how often" and "is it
    /// safe" collapses onto that same sentence, which answers none of them.
    /// </para>
    /// <para>
    /// This does not make advise per-question, and deliberately so: the suggestion is grounded in a
    /// published guideline at generation time and the model is made to name which, machinery no
    /// path inside a caregiver's wait reproduces. It lets the reply say that the row is a standing
    /// suggestion rather than an answer to the specific question, which is the difference between
    /// being unhelpful and being misleading.
    /// </para>
    /// <para>
    /// Whole words, like the topic matchers above and for the same reason — "which" inside
    /// "sandwich" is not a caregiver asking which.
    /// </para>
    /// </remarks>
    public static bool AsksForSpecifics(string question) => SpecificsWords().IsMatch(question);

    [GeneratedRegex(
        @"\b(?:what kind|what kinds|what sort|what type|which|how much|how many|how often|"
        + @"how long|how far|how many times|is it safe|are they safe|is that safe)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecificsWords();

    /// <summary>
    /// The row to serve: the named topic's, else the general one, else the most recent — each
    /// step over servable rows only, so no fallback ever serves what the details card would
    /// withhold.
    /// </summary>
    public static MemberAdvise? Pick(string question, IReadOnlyList<MemberAdvise> rows, DateTime utcNow)
    {
        var servable = rows.Where(r => AdviseServability.IsServable(r, utcNow)).ToList();
        if (servable.Count == 0)
            return null;

        var topic = TopicOf(question);
        return (topic is { } t ? servable.FirstOrDefault(r => r.Topic == t) : null)
            ?? Fallback(servable);
    }

    /// <summary>The row a reader with no question serves — the details card and the dashboard
    /// indicator: the general row when there is one, else the most recent servable.</summary>
    public static MemberAdvise? PickDefault(IReadOnlyList<MemberAdvise> rows, DateTime utcNow)
    {
        var servable = rows.Where(r => AdviseServability.IsServable(r, utcNow)).ToList();
        return servable.Count == 0 ? null : Fallback(servable);
    }

    private static MemberAdvise? Fallback(List<MemberAdvise> servable) =>
        servable.FirstOrDefault(r => r.Topic == AdviseTopic.General)
            ?? servable.MaxBy(r => r.GeneratedAtUtc);
}
