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
    /// The wearable reading the question is about, when the router named one that parsed. Null
    /// when the question is about how the person is in general, when it asked for every reading
    /// (<see cref="AllReadings"/>), or when the label was missing or unknown — unknown drops,
    /// never coerced, the same as <see cref="ParseLabel"/>.
    /// </summary>
    public StatusMetric? NamedMetric { get; init; }

    /// <summary>
    /// True when the question asked for the readings themselves rather than one named figure or
    /// how the person is. Distinct from an omitted <see cref="NamedMetric"/>: omit is "how is he",
    /// which serves the stored line; this is "his numbers", which serves the dated list.
    /// </summary>
    public bool AllReadings { get; init; }

    /// <summary>
    /// The wellbeing area an advise question named, when the router named one that parsed. Null
    /// when the question named none, when the workflow is not advise, or when the label was
    /// unknown — the picker then falls back to the general row, then the most recent servable.
    /// </summary>
    public AdviseTopic? AdviseTopic { get; init; }

    /// <summary>
    /// True when an advise question asks which, how much, how often, or whether something is
    /// safe — shapes a standing, pre-generated suggestion cannot answer. The row is still served;
    /// the reply says it is a standing suggestion rather than an answer to that question.
    /// </summary>
    public bool AsksForSpecifics { get; init; }

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
    /// True when the two candidates are <c>advise</c> and one of the steers — a pair that
    /// <see cref="NeedsClarify"/> calls a different ask, and which the dispatch resolves without
    /// asking whenever there is a suggestion to serve.
    /// </summary>
    /// <remarks>
    /// A steer is a redirect, not an answer: it says what the app cannot do and points at what it
    /// can. A servable suggestion is one of the things it can do. Offering the caregiver a choice
    /// between the two — "something outside their health data, or a suggestion for what could
    /// help?" — asks them to pick between being turned away and being answered, which is not a
    /// real ambiguity (observed 2026-09-07 on "what kind of exercises can he do"). Whether there
    /// <em>is</em> a suggestion needs a lookup this record cannot make, so the rule lives in the
    /// dispatch and this only names the shape; with no row the pair clarifies down to the steer
    /// through the dead-branch rule, as before.
    /// </remarks>
    public bool PitsAdviseAgainstASteer =>
        Primary is { } p && RunnerUp is { } r
        && ((p == MemberChatWorkflow.Advise && IsSteer(r)) || (r == MemberChatWorkflow.Advise && IsSteer(p)));

    private static bool IsSteer(MemberChatWorkflow workflow) => workflow
        is MemberChatWorkflow.SteerCasual
        or MemberChatWorkflow.SteerOffTopic;

    /// <summary>
    /// The ladder's neighbour relation. The five ladder rungs are ranked; the steer entries sit off
    /// the ladder entirely, so a steer against any ladder rung is never adjacent — "either steer
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

    /// <summary>
    /// The closed labels the routing prompt offers for <see cref="NamedMetric"/> / all-readings.
    /// The prompt renders this list; <see cref="ParseMetric"/> and <see cref="ParseAllReadings"/>
    /// accept only these — one object serving both, the same way the catalogue serves the workflow.
    /// </summary>
    public static IReadOnlyList<string> MetricLabels { get; } =
        ["steps", "restingHeartRate", "hrv", "oxygen", "breathing", "sleep", "all"];

    /// <summary>The closed labels the routing prompt offers for <see cref="AdviseTopic"/>.</summary>
    public static IReadOnlyList<string> AdviseTopicLabels { get; } =
        ["activity", "sleep", "heart"];

    /// <summary>
    /// Maps a <c>namedMetric</c> label to the reading it names. <c>all</c> and unknown names
    /// drop to null — <c>all</c> is <see cref="ParseAllReadings"/>'s job, not a sixth metric.
    /// </summary>
    public static StatusMetric? ParseMetric(string? label) => Canonical(label) switch
    {
        "steps" => StatusMetric.Steps,
        "restingheartrate" => StatusMetric.RestingHeartRate,
        "hrv" => StatusMetric.HeartRateVariability,
        "oxygen" => StatusMetric.Oxygen,
        "breathing" => StatusMetric.BreathingRate,
        "sleep" => StatusMetric.Sleep,
        _ => null,
    };

    /// <summary>True only for the exact <c>all</c> label — unknown names are not a readings request.</summary>
    public static bool ParseAllReadings(string? label) => Canonical(label) == "all";

    /// <summary>
    /// Maps an <c>adviseTopic</c> label to the stored-suggestion topic it names. Unknown names
    /// drop to null, never to <see cref="CardiTrack.Domain.Enums.AdviseTopic.General"/> — general
    /// is the picker's fallback, not a thing the router is asked to name.
    /// </summary>
    public static CardiTrack.Domain.Enums.AdviseTopic? ParseAdviseTopic(string? label) => Canonical(label) switch
    {
        "activity" => CardiTrack.Domain.Enums.AdviseTopic.Activity,
        "sleep" => CardiTrack.Domain.Enums.AdviseTopic.Sleep,
        "heart" => CardiTrack.Domain.Enums.AdviseTopic.HeartRate,
        _ => null,
    };

    private static string? Canonical(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim().ToLowerInvariant();
}
