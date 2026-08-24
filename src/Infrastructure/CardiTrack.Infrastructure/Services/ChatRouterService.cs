using System.ComponentModel;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The lean routing call — one structured generation on the Rewrite slot whose entire vocabulary
/// is the catalogue's rendered purpose lines. Everything else about the design is scaffolding
/// around those lines (see <c>docs/technical/member_chat_routing.md</c> §4); they are rendered
/// from <see cref="ChatWorkflowCatalogue.Routable"/> at call time, so an entry becoming
/// implemented starts rendering here without this file changing.
/// </summary>
/// <remarks>
/// Carries no registry, no member context and no data vocabulary, deliberately: dataset selection
/// needs to know which workflow is running to be any good, and at routing time that is precisely
/// what is not yet known. The prompt is the purpose lines, the ladder's tie-break, the question,
/// and the caregiver's prior questions when there are any.
/// </remarks>
public class ChatRouterService : IChatRouter
{
    private readonly IRewriteAiService _rewriteAi;

    public ChatRouterService(IRewriteAiService rewriteAi) => _rewriteAi = rewriteAi;

    public async Task<AiGenerationResult<ChatRouteDecision>> RouteAsync(
        string question, string? questionsOnlyHistory = null, CancellationToken ct = default)
    {
        var result = await _rewriteAi.GenerateStructuredWithUsageAsync<ChatRouteAiResponse>(
            BuildPrompt(question, questionsOnlyHistory), ct);

        var decision = new ChatRouteDecision
        {
            Primary = ChatRouteDecision.ParseLabel(result.Result.Workflow),
            RunnerUp = ChatRouteDecision.ParseLabel(result.Result.RunnerUp),
        };

        return new AiGenerationResult<ChatRouteDecision>(decision, result.Usage);
    }

    internal static string BuildPrompt(string question, string? questionsOnlyHistory)
    {
        var entries = string.Join("\n", ChatWorkflowCatalogue.Routable
            .Select(w => $"- {w.Label}: {w.Purpose}"));

        var historySection = questionsOnlyHistory is null
            ? string.Empty
            : $"""

              The question may be a short follow-up — read it against what the caregiver already
              asked below, and route it by what it means there.

              {questionsOnlyHistory}
              """;

        // The ordering paragraph states the ladder's tie-break rather than re-describing each
        // entry: when two neighbouring ways both fit, the one that claims less serves the
        // caregiver either way, so the router is told to take it instead of agonising.
        return $"""
            A family caregiver sent the message below inside a health-monitoring app about their
            family member. Classify which one way of answering it should be used. Do not answer the
            question itself, and do not choose data — only classify.

            The ways of answering, from least to most they claim:
            {entries}

            These form a ladder: each claims more than the one before it. When two neighbouring
            ways both fit, choose the one that claims less. Only name a runnerUp when a genuinely
            different way of answering also fits — not a neighbour you already resolved by taking
            the lower.
            {historySection}

            --- Caregiver message ---
            {question}

            Respond with:
            - workflow: the one way of answering, exactly as named above.
            - runnerUp: a second way that also genuinely fits, exactly as named above, or omit it.
            """ + MedicalPromptBlocks.ChatMessageGuardrail;
    }

    internal sealed record ChatRouteAiResponse
    {
        [Description("The chosen way of answering, exactly as named in the list given.")]
        public required string Workflow { get; init; }

        [Description("A second way that also genuinely fits, exactly as named in the list, "
            + "omitted when nothing else fits.")]
        public string? RunnerUp { get; init; }
    }
}
