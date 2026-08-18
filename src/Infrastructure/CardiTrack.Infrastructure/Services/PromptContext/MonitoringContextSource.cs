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

        // activeOnly is IsActive, which a resolved alert stays — resolution is what ends the
        // episode, so the !IsResolved filter is the one that matters here. Same shape as
        // DashboardService's unresolved-alert read.
        var unresolved = (await _unitOfWork.Alerts.GetByCardiMemberAsync(request.CardiMemberId, activeOnly: true))
            .Where(a => !a.IsResolved)
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
            text = $"{text[..MaxAssessmentTextLength]}…";

        return $"- Automated observation ({assessment.Severity}, {HoursAgo(assessment.WindowEndUtc, utcNow)}): {text}";
    }

    private static string HoursAgo(DateTime thenUtc, DateTime utcNow)
    {
        var hours = (int)Math.Floor((utcNow - thenUtc).TotalHours);
        return hours <= 0 ? "within the hour" : $"{hours}h ago";
    }
}
