using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// What the monitoring service itself has noticed lately: assessments the assessor rated worth
/// noting, and alerts nobody has resolved. The section that stops a family summary reassuring a
/// caregiver about a member the same service is currently alerting them about.
/// </summary>
/// <remarks>
/// <para>
/// This closes a contract the pipeline had been making and not keeping. The assessor's prompt tells
/// the model that "medium means worth mentioning in the daily summary", and docs/llm_design.md
/// routes Medium severity to the digest — but the digest read no assessments at all, so a medium
/// finding went into a table and stopped there. Alerts only fire at Orange and above, which left
/// everything below it with nowhere to go.
/// </para>
/// <para>
/// Digest only. The hero status line already composes its own severity tier from unresolved alerts;
/// the alert insight is about one specific alert; and feeding the assessor its own past assessments
/// would close a loop where the model reads yesterday's wording back as though it were evidence.
/// </para>
/// </remarks>
internal sealed class MonitoringContextSource : IMemberContextSource
{
    /// <summary>
    /// The heading this section sits under. Load-bearing: the digest prompt names it when it
    /// tells the model to surface an unresolved alert, and when it says never to follow
    /// instructions in it.
    /// </summary>
    internal const string SectionLabel = "Recent monitoring context";

    /// <summary>How far back an assessment still describes "lately" for a daily summary.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// The lowest severity worth carrying into a family summary. Yellow is Medium on the pipeline's
    /// internal scale (docs/llm_design.md severity mapping) — the tier the assessor's own prompt
    /// promises will be mentioned. Below it is "all is well", which the summary says anyway when
    /// there is nothing here to say otherwise.
    /// </summary>
    private const AlertSeverity MinimumSeverity = AlertSeverity.Yellow;

    /// <summary>
    /// Most assessments carried per digest. A member having a bad afternoon can produce a dozen
    /// notable windows, and a prompt listing all of them buys nothing over the newest few while
    /// crowding out the readings — the summary is about the day, not a log of it.
    /// </summary>
    private const int MaxAssessments = 5;

    /// <summary>
    /// Most unresolved alerts carried per digest, for the reason <see cref="MaxAssessments"/>
    /// gives — and for a second one.
    /// </summary>
    /// <remarks>
    /// Alerts are rendered before assessments, and <c>MemberContextComposer</c> caps the whole
    /// section. Uncapped, a member with a long tail of open alerts pushed every automated
    /// observation past that cap and lost them entirely — the one thing this source was added to
    /// deliver, silently dropped for exactly the members with the most going on. Newest first, so
    /// what survives the cap is what is still current.
    /// </remarks>
    private const int MaxAlerts = 5;

    /// <summary>Per-assessment cap: enough for the finding, not the whole caregiver message.</summary>
    private const int MaxAssessmentTextLength = 200;

    private readonly IUnitOfWork _unitOfWork;

    public MonitoringContextSource(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    // Not the daybook: this source is anchored on "the last 24 hours from now", which is the
    // wrong day for an account of yesterday — the daybook builds its own day-scoped monitoring
    // section from the alerts and assessments of the reviewed day itself (DaybookPrompt).
    public PromptPurpose Purposes => PromptPurpose.Digest;

    public int Order => 20;

    public async Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct)
    {
        var assessments = await _unitOfWork.RealtimeAssessments
            .GetSinceAsync(request.CardiMemberId, request.UtcNow - Window, ct);

        var notable = assessments
            .Where(a => a.Severity >= MinimumSeverity)
            .Take(MaxAssessments)
            .ToList();

        // The same read DashboardService, CardiMemberService and member chat use: unresolved is
        // IsActive && !IsResolved, done in SQL, untracked, with a stable tie-break for the
        // several alerts one member can be given in the same instant. This used to fetch every
        // active alert tracked and filter the resolved ones off in memory, which loaded rows it
        // discarded and left entities being tracked by a DbContext that only ever reads them.
        var unresolved = (await _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(request.CardiMemberId))
            .Take(MaxAlerts)
            .ToList();

        // Nothing to say means no section at all, rather than a section saying nothing. "Do not
        // mention monitoring when all is quiet" is then structural: on a calm member the words are
        // not in the prompt to be echoed, which is a stronger guarantee than instructing it.
        if (notable.Count == 0 && unresolved.Count == 0)
            return null;

        var lines = new List<string>();
        lines.AddRange(unresolved.Select(a => DescribeAlert(a, request.UtcNow)));
        lines.AddRange(notable.Select(a => DescribeAssessment(a, request.UtcNow)));

        return new MemberContextSection(SectionLabel, string.Join("\n", lines));
    }

    private static string DescribeAlert(Alert alert, DateTime utcNow) =>
        $"- Unresolved alert ({alert.Severity}, {alert.AlertType}, raised {HoursAgo(alert.TriggeredDate, utcNow)}): "
        + MedicalPromptBlocks.Flatten(alert.Title);

    private static string DescribeAssessment(RealtimeAssessment assessment, DateTime utcNow)
    {
        var text = MedicalPromptBlocks.Flatten(assessment.ModelOutput);
        if (text.Length > MaxAssessmentTextLength)
            text = $"{MedicalPromptBlocks.CutTo(text, MaxAssessmentTextLength)}…";

        return $"- Automated observation ({assessment.Severity}, {HoursAgo(assessment.WindowEndUtc, utcNow)}): {text}";
    }

    private static string HoursAgo(DateTime thenUtc, DateTime utcNow)
    {
        var hours = (int)Math.Floor((utcNow - thenUtc).TotalHours);
        return hours <= 0 ? "within the hour" : $"{hours}h ago";
    }
}
