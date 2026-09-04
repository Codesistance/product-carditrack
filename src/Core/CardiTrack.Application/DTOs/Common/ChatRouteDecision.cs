using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// What the routing call decided: which workflow serves the question, and the runner-up when the
/// model named one. The clarify judgement is computed here, not asked of the model — the router
/// never returns <see cref="MemberChatWorkflow.Clarify"/>; the app decides from the shape of this
/// answer (see <c>docs/technical/member_chat_routing.md</c> §5).
/// </summary>
public sealed record ChatRouteDecision
{
    /// <summary>The chosen workflow, or null when the answer did not parse — the caller descends
    /// to analysis, the failure target for everything unsure.</summary>
    public required MemberChatWorkflow? Primary { get; init; }

    /// <summary>The model's second choice, when it offered one that parsed.</summary>
    public MemberChatWorkflow? RunnerUp { get; init; }

    /// <summary>
    /// True when the two candidates are different <em>asks</em> — genuine confusion about what was
    /// wanted, which is what clarify exists for. Two things absorb ambiguity rather than asking:
    /// adjacent rungs, which the ladder's tie-break takes downward, and any pair of reading rungs,
    /// which are one ask answered at different heights. Firing on either would fire on precisely
    /// the cases designed to be safe to get wrong.
    /// </summary>
    public bool NeedsClarify =>
        Primary is { } p && RunnerUp is { } r && r != p
        && !AreAdjacent(p, r)
        && !AreBothReadings(p, r);

    /// <summary>
    /// The ladder's neighbour relation. The five data rungs are ranked; the steer entries sit off
    /// the ladder entirely, so a steer against any data rung is never adjacent — "either steer
    /// entry against analysis" is §5's own example of what should clarify. The two steers are
    /// adjacent to each other: both answer without data, and confusing them costs a redirect
    /// where a warm reply belonged, which is an eval-set concern rather than a clarify one.
    /// </summary>
    internal static bool AreAdjacent(MemberChatWorkflow a, MemberChatWorkflow b)
    {
        int? Rung(MemberChatWorkflow w) => w switch
        {
            MemberChatWorkflow.Status => 1,
            MemberChatWorkflow.Analysis => 2,
            MemberChatWorkflow.Inference => 3,
            MemberChatWorkflow.Investigation => 4,
            MemberChatWorkflow.Advise => 5,
            _ => null,
        };

        var (ra, rb) = (Rung(a), Rung(b));
        if (ra is { } x && rb is { } y)
            return Math.Abs(x - y) == 1;

        // Off-ladder entries: adjacent only to each other.
        return ra is null && rb is null;
    }

    /// <summary>
    /// The reading rungs — <c>status</c>, <c>analysis</c>, <c>inference</c>, <c>investigation</c>.
    /// All four answer one question, "what do this person's readings say?", differing only in how
    /// much claim the answer makes, and each returns the figures the rung below it would have. So a
    /// runner-up drawn from this set is never confusion about what was asked, only about how far up
    /// to go — the superset argument §5 makes for analysis against inference, which holds just as
    /// well two rungs apart. "How is he today" is the case that forced this: <c>status</c> against
    /// <c>inference</c> is a distance of two and a single question, and asking there fires clarify
    /// on the most common message the app receives, against §8's rule that clarify is only worth
    /// having while it is rare. <c>advise</c> is deliberately not in this set — it claims a
    /// suggestion rather than a reading, which is why §5's own example, <c>status</c> against
    /// <c>advise</c>, still clarifies.
    /// </summary>
    internal static bool AreBothReadings(MemberChatWorkflow a, MemberChatWorkflow b) =>
        IsReading(a) && IsReading(b);

    private static bool IsReading(MemberChatWorkflow workflow) => workflow
        is MemberChatWorkflow.Status
        or MemberChatWorkflow.Analysis
        or MemberChatWorkflow.Inference
        or MemberChatWorkflow.Investigation;

    /// <summary>
    /// Maps a label the model returned to the workflow it names — the catalogue's own labels, and
    /// only entries the router is allowed to return. An unknown or unroutable name is dropped to
    /// null, never coerced: the same closed-vocabulary discipline as every other model-output
    /// parse on this platform.
    /// </summary>
    public static MemberChatWorkflow? ParseLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var match = ChatWorkflowCatalogue.Routable
            .FirstOrDefault(w => string.Equals(w.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }
}
