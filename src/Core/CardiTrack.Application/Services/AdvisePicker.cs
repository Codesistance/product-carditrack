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
/// The topic comes from the routing call, which already spent the one classification this turn
/// gets. A miss — the router named nothing, or named a topic with no servable row — falls back
/// rather than failing: named topic first, then the general row, then the most recent servable
/// anything. A caregiver asking about sleep with only an activity suggestion on file gets the
/// activity suggestion rather than nothing, because <c>AdviseReply</c>'s empty case only honestly
/// applies when there is nothing at all to serve.
/// </remarks>
public static class AdvisePicker
{
    /// <summary>
    /// The row to serve: the named topic's, else the general one, else the most recent — each
    /// step over servable rows only, so no fallback ever serves what the details card would
    /// withhold.
    /// </summary>
    public static MemberAdvise? Pick(AdviseTopic? topic, IReadOnlyList<MemberAdvise> rows, DateTime utcNow)
    {
        var servable = rows.Where(r => AdviseServability.IsServable(r, utcNow)).ToList();
        if (servable.Count == 0)
            return null;

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
