using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// What kind of sentence an entry is licensed to produce. The only place that limit is written
/// down — until now it was held entirely by which tone block each prompt happened to carry, a
/// convention that holds because people remember it.
/// </summary>
public enum ChatClaimClass
{
    /// <summary>States a value or a state. "He took 6,200 steps today."</summary>
    Observation = 1,

    /// <summary>States where a value sits against a reference. "About a third below his usual",
    /// "below the AHA's typical adult range" — never whether that is good.</summary>
    Comparison = 2,

    /// <summary>States whether something is settled or worth attention, and must name what that
    /// rests on.</summary>
    Judgement = 3,

    /// <summary>Recommends an action. Reachable only by serving a suggestion grounded ahead of
    /// time; no chat prompt may compose one.</summary>
    Suggestion = 4,

    /// <summary>Says nothing about the member at all.</summary>
    None = 5,
}

/// <summary>One entry as the router sees it, plus the limits the handler is held to.</summary>
/// <param name="Purpose">
/// The line rendered into the routing prompt. These lines <em>are</em> the routing prompt — see
/// <c>docs/technical/member_chat_routing.md</c> §4 — and they get rewritten against the eval set
/// rather than reasoned into correctness.
/// </param>
/// <param name="AllowedDatasets">
/// What this entry's planner may ask for. The validator intersects the model's answer with this,
/// so the catalogue the router reads and the rule the validator enforces are one object and cannot
/// drift.
/// </param>
/// <param name="IsRoutable">
/// False for a handler the app triggers rather than the router choosing — today only
/// <see cref="MemberChatWorkflow.Clarify"/>. Unroutable entries are never rendered into the prompt.
/// </param>
/// <param name="IsImplemented">
/// False for an id reserved ahead of its handler. Never rendered into the prompt, so the router
/// cannot pick something nothing can run.
/// </param>
public sealed record ChatWorkflowDefinition(
    MemberChatWorkflow Id,
    string Purpose,
    ChatClaimClass ClaimClass,
    IReadOnlyList<DataQueryKind> AllowedDatasets,
    bool IsRoutable = true,
    bool IsImplemented = true);

/// <summary>
/// The member-chat workflow vocabulary: what the router may return, what each entry may claim, and
/// what data each may ask for. Data, not code — every rendered id is meant to have a registered
/// handler, and the pair ships together.
/// </summary>
/// <remarks>
/// <para>
/// What the architecture parity test actually enforces today is the vocabulary and the claim
/// limits — the catalogue agrees with <see cref="MemberChatWorkflow"/> in both directions, only
/// Advise may claim <see cref="ChatClaimClass.Suggestion"/>, an entry that recommends or says
/// nothing fetches nothing, anything licensed to judge can reach a baseline, and clarify is never
/// rendered. The handler half — every rendered id has a handler, every handler is routable or
/// explicitly app-triggered — waits for those handlers to become types; they are private methods
/// on <c>MemberChatService</c> today, so there is nothing to reflect over.
/// </para>
/// <para>
/// Constants in the Application layer, reviewed like an alert rule — see
/// <see cref="AlertRuleCatalogue"/>, whose reserved-id pattern this follows. Changing what an entry
/// may claim should take the same review as changing an alert threshold, because it is the same
/// kind of decision.
/// </para>
/// </remarks>
public static class ChatWorkflowCatalogue
{
    /// <summary>Every entry, including the unimplemented and the unroutable.</summary>
    public static IReadOnlyList<ChatWorkflowDefinition> All { get; } =
        Array.AsReadOnly(new ChatWorkflowDefinition[]
        {
            new(MemberChatWorkflow.Status,
                "One reading, or one moment — what a value currently is, or when something last "
                + "happened. No comparison with what is usual for this person and no judgement "
                + "about whether it is good. Also covers device, sync and monitoring state. A "
                + "question spanning several days is not this.",
                ChatClaimClass.Observation,
                [DataQueryKind.RecentActivity]),

            new(MemberChatWorkflow.Analysis,
                "What the readings say over a period, set against what is usual for this member "
                + "and against the published typical range where one exists. Choose this when "
                + "answering needs arithmetic over a window.",
                ChatClaimClass.Comparison,
                [DataQueryKind.RecentActivity, DataQueryKind.Baseline, DataQueryKind.RealtimeAssessments]),

            new(MemberChatWorkflow.Inference,
                "Whether what the readings show is settled or worth attention. Choose this when "
                + "the question asks for a verdict rather than for figures. It returns the figures "
                + "as well.",
                ChatClaimClass.Judgement,
                [DataQueryKind.RecentActivity, DataQueryKind.Baseline, DataQueryKind.UnresolvedAlerts, DataQueryKind.RealtimeAssessments]),

            // Reserved ahead of its handler: the two-pass fetch, the consent gate and the
            // co-occurrence rule are not built, and §10's off-ramp decides whether they will be.
            new(MemberChatWorkflow.Investigation,
                "Why something changed, and what co-occurred with it. Choose this only when the "
                + "question asks to explain a change and answering would mean looking at things "
                + "the question did not name.",
                ChatClaimClass.Judgement,
                [DataQueryKind.RecentActivity, DataQueryKind.Baseline, DataQueryKind.UnresolvedAlerts, DataQueryKind.RealtimeAssessments],
                IsImplemented: false),

            new(MemberChatWorkflow.Advise,
                "What could be done about the member's wellbeing. Choose this when answering would "
                + "mean recommending an action.",
                ChatClaimClass.Suggestion,
                []),

            new(MemberChatWorkflow.SteerCasual,
                "Not a question at all — a greeting, thanks, small talk, or a question about the "
                + "assistant itself.",
                ChatClaimClass.None,
                []),

            new(MemberChatWorkflow.SteerOffTopic,
                "A genuine request, but about something unrelated to this person's health or care.",
                ChatClaimClass.None,
                []),

            // Triggered by the shape of the routing answer — a non-adjacent runner-up, or a result
            // that cannot be run — never returned by the model. Adjacent ambiguity is what the
            // ladder's tie-break is for.
            new(MemberChatWorkflow.Clarify,
                "The router could not place the question; ask which rung was meant.",
                ChatClaimClass.None,
                [],
                IsRoutable: false),
        });

    /// <summary>
    /// What the routing prompt renders: implemented entries the router may actually return.
    /// </summary>
    /// <remarks>
    /// Filtered on both flags rather than one. An unimplemented id would route to nothing; an
    /// unroutable one would let the model pick a handler the app is supposed to trigger itself.
    /// </remarks>
    public static IReadOnlyList<ChatWorkflowDefinition> Routable { get; } =
        Array.AsReadOnly(All.Where(w => w.IsRoutable && w.IsImplemented).ToArray());

    /// <summary>The entry for an id, or null when the id is not in the catalogue.</summary>
    public static ChatWorkflowDefinition? Find(MemberChatWorkflow id) =>
        All.FirstOrDefault(w => w.Id == id);
}
