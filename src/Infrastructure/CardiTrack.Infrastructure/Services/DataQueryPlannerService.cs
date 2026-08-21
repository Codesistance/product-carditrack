using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Asks the split, non-medical Rewrite model which of the closed <see cref="DataQueryKind"/>
/// sources a caregiver's question needs, then defensively parses the reply into
/// <see cref="DataQueryPlan"/>. The model never sees or is asked for a member/user identifier — the
/// prompt below does not offer one, and <see cref="DataQueryPlan"/>'s shape could not carry one back
/// even if the model tried (see that type's remarks).
/// </summary>
public class DataQueryPlannerService : IDataQueryPlanner
{
    private static readonly IReadOnlyDictionary<DataQueryKind, string> SourceDescriptions = new Dictionary<DataQueryKind, string>
    {
        [DataQueryKind.RecentActivity] = "RecentActivity — daily steps, heart rate and sleep over the last several days",
        [DataQueryKind.Baseline] = "Baseline — the member's own established behavioural pattern (typical steps, heart rate, sleep)",
        [DataQueryKind.UnresolvedAlerts] = "UnresolvedAlerts — alerts raised for this member that nobody has acknowledged yet",
        [DataQueryKind.RealtimeAssessments] = "RealtimeAssessments — recent hour-by-hour heart-rate severity assessments",
    };

    private readonly IRewriteAiService _rewriteAi;

    public DataQueryPlannerService(IRewriteAiService rewriteAi) => _rewriteAi = rewriteAi;

    public async Task<AiGenerationResult<DataQueryPlan>> PlanAsync(
        string question, string? conversationHistory = null, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(question, conversationHistory);
        var result = await _rewriteAi.GenerateStructuredWithUsageAsync<DataQueryPlanAiResponse>(prompt, ct);

        return new AiGenerationResult<DataQueryPlan>(Parse(result.Result), result.Usage);
    }

    private static string BuildPrompt(string question, string? conversationHistory)
    {
        var sourceList = string.Join("\n", SourceDescriptions.Select(kv => $"- {kv.Value}"));
        // Framed history, not raw turns — the same block the clinical prompt gets (see
        // MemberChatService.BuildHistoryBlockAsync). Without it a follow-up like "and that week's
        // sleep?" carries its window only in turns this prompt never saw, so the planner answered
        // the defaults instead of the question.
        var historySection = conversationHistory is null
            ? string.Empty
            : $"""

              The question may be a follow-up — read it in the context of what was already asked
              and answered below.

              {conversationHistory}
              """;
        return $"""
            A family caregiver asked a question about a person whose wearable and health data this
            service already holds. Decide which of these existing data sources are needed to answer
            it — pick only what the question actually needs, not everything available.

            Available sources:
            {sourceList}

            You are not told who the person is and must not ask — the system already knows and will
            fetch the sources you name for the right person. Name sources only, never a person.
            {historySection}

            --- Caregiver question ---
            {question}

            Respond with the source names from the list above (as written), and if RecentActivity is
            needed, how many days back is relevant (default 7, at most 14); if RealtimeAssessments is
            needed, how many hours back is relevant (default 24, at most 72).

            Also name which specific daily metrics the question is about, as metrics: any of Steps,
            RestingHeartRate, Sleep. Name only the ones the question actually asks about — a
            question about steps names Steps alone. Leave metrics empty for a general question
            about how the person is doing overall.
            """;
    }

    /// <summary>
    /// Unrecognised source names are dropped, not thrown — a defensive parse of model output, the
    /// same discipline <c>AssessmentSeverityParser.Map</c> applies to MedGemma's severity strings.
    /// A plan with zero recognised sources is valid: it means the clinical step answers from the
    /// member's demographic/notes context alone (always composed in — see
    /// <c>PromptContext.MemberContextComposer</c>), which is correct for a question that does not
    /// need any of the four data sources at all.
    /// </summary>
    private static DataQueryPlan Parse(DataQueryPlanAiResponse response)
    {
        var sources = response.Sources
            .Select(s => Enum.TryParse<DataQueryKind>(s, ignoreCase: true, out var kind) ? kind : (DataQueryKind?)null)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();

        // Same defensive parse as the sources, but the empty cases are kept apart (see
        // DataQueryPlan.ChartMetrics): an absent field is null — the model did not answer — and
        // so is a list whose every name failed to parse, because names we could not read tell us
        // nothing about what the question was. Only a list the model deliberately sent empty
        // means "general question".
        IReadOnlyList<ChartMetricKind>? metrics = null;
        if (response.Metrics is { } named)
        {
            var recognised = named
                .Select(m => Enum.TryParse<ChartMetricKind>(m, ignoreCase: true, out var kind) ? kind : (ChartMetricKind?)null)
                .Where(m => m is not null)
                .Select(m => m!.Value)
                .Distinct()
                .ToList();

            metrics = named.Count > 0 && recognised.Count == 0 ? null : recognised;
        }

        return new DataQueryPlan
        {
            Sources = sources,
            RecentActivityDays = response.RecentActivityDays is > 0 ? response.RecentActivityDays.Value : 7,
            RealtimeAssessmentHours = response.RealtimeAssessmentHours is > 0 ? response.RealtimeAssessmentHours.Value : 24,
            ChartMetrics = metrics,
        };
    }

    internal sealed record DataQueryPlanAiResponse
    {
        public required IReadOnlyList<string> Sources { get; init; }
        public int? RecentActivityDays { get; init; }
        public int? RealtimeAssessmentHours { get; init; }
        public IReadOnlyList<string>? Metrics { get; init; }
    }
}
