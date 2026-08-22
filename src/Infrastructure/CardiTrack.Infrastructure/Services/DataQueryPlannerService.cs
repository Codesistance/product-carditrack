using System.ComponentModel;
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
        [DataQueryKind.RecentActivity] = "RecentActivity — daily steps, heart rate, sleep and overnight heart rate variability over the last several days",
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
            needed, how many days back is relevant — 1 to 7, default 7, and a question about a
            longer stretch still gets the last 7 days; if RealtimeAssessments is needed, how many
            hours back is relevant (default 24, at most 72).

            Always answer metrics, naming which specific daily readings the question is about: any
            of Steps, RestingHeartRate, Sleep, HeartRateVariability. Name every one the question
            asks about and no others — "how many steps has he done?" is ["Steps"], "how did he
            sleep and what was his heart rate?" is ["Sleep","RestingHeartRate"]. Only a question
            about how the person is doing overall, naming no particular reading, gets an empty
            list. These become the charts drawn under the answer, so an empty list where the
            question named a reading puts charts in front of the caregiver that they did not ask
            for.
            """ + MedicalPromptBlocks.ChatMessageGuardrail;
    }

    /// <summary>
    /// Unrecognised source names are dropped, not thrown — a defensive parse of model output, the
    /// same discipline <c>AssessmentSeverityParser.Map</c> applies to MedGemma's severity strings.
    /// A plan with zero recognised sources is valid: it means the clinical step answers from the
    /// member's demographic/notes context alone (always composed in — see
    /// <c>PromptContext.MemberContextComposer</c>), which is correct for a question that does not
    /// need any of the four data sources at all.
    /// </summary>
    internal static DataQueryPlan Parse(DataQueryPlanAiResponse response)
    {
        // IsDefined alongside TryParse, not instead of it: TryParse succeeds on any numeric
        // string, so "999" would parse to (DataQueryKind)999 and travel on as a recognised source
        // that the whitelist then matches nothing against — a silent empty fetch wearing the
        // shape of a real plan. Only names of members that actually exist survive.
        var sources = response.Sources
            .Select(s => Enum.TryParse<DataQueryKind>(s, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                ? kind
                : (DataQueryKind?)null)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();

        // Same defensive parse as the sources, but the empty cases are kept apart (see
        // DataQueryPlan.ChartMetrics). The field is required now, so "not answered" no longer
        // means absent — it means a list whose every name failed to parse, since names we could
        // not read tell us nothing about what the question was. An empty list is the model
        // deliberately saying "general question".
        var recognisedMetrics = response.Metrics
            .Select(m => Enum.TryParse<ChartMetricKind>(m, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                ? kind
                : (ChartMetricKind?)null)
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .Distinct()
            .ToList();

        var metrics = response.Metrics.Count > 0 && recognisedMetrics.Count == 0
            ? null
            : (IReadOnlyList<ChartMetricKind>)recognisedMetrics;

        return new DataQueryPlan
        {
            Sources = sources,
            RecentActivityDays = response.RecentActivityDays is > 0 ? response.RecentActivityDays.Value : 7,
            RealtimeAssessmentHours = response.RealtimeAssessmentHours is > 0 ? response.RealtimeAssessmentHours.Value : 24,
            ChartMetrics = metrics,
        };
    }

    /// <remarks>
    /// The descriptions are not decoration: <c>StructuredOutputSchema</c> copies them into the
    /// schema the model is constrained by, and without them a field's own name is the only account
    /// of what belongs in it. <c>metrics</c> is the field that proved this — left undescribed and
    /// optional, a question as plain as "show me his steps this week" came back with it omitted,
    /// and every chart was drawn because an unanswered field widens.
    /// </remarks>
    internal sealed record DataQueryPlanAiResponse
    {
        [Description("Which data sources the question needs, by name, from the list given. May be empty.")]
        public required IReadOnlyList<string> Sources { get; init; }

        [Description("How many days of daily readings the question is about, 1 to 7.")]
        public int? RecentActivityDays { get; init; }

        [Description("How many hours of real-time assessments the question is about, 1 to 72.")]
        public int? RealtimeAssessmentHours { get; init; }

        /// <summary>
        /// Required so that "the question is general" has to be said rather than skipped. An
        /// omitted field and a deliberately empty one both widen to every chart, and while that is
        /// the right failure when nothing is known, it is the wrong answer to a question that
        /// named its metric perfectly clearly.
        /// </summary>
        [Description(
            "Which daily metrics the question asks about: any of Steps, RestingHeartRate, Sleep. "
            + "Name every metric the question is about and no others — a question about steps is "
            + "[\"Steps\"]. Empty only when the question is about how the person is doing overall.")]
        public required IReadOnlyList<string> Metrics { get; init; }
    }
}
