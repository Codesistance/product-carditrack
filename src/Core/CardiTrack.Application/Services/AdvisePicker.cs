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
public static class AdvisePicker
{
    /// <summary>The topic a question names, or null when it names none.</summary>
    public static AdviseTopic? TopicOf(string question)
    {
        var q = question.ToLowerInvariant();

        bool Any(params string[] words) => words.Any(q.Contains);

        if (Any("sleep", "slept", "sleeping", "night", "nap", "rest", "bed"))
            return AdviseTopic.Sleep;
        if (Any("walk", "step", "activity", "active", "exercise", "moving", "move"))
            return AdviseTopic.Activity;
        if (Any("heart", "pulse", "bpm", "cardiac"))
            return AdviseTopic.HeartRate;

        return null;
    }

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
