using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The daily trend pass: deterministic features computed in .NET, read by MedGemma against the
/// pinned reference ranges, stored as the member's <see cref="InsightScope.Trend"/> insight.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>TrendInterpreter</c> of <c>docs/llm_design.md</c>, and it is what the per-user
/// LSTM was replaced by when that was dropped on 2026-08-10. The three layers are deliberately
/// separable: <see cref="TrendFeatureCalculator"/> computes every number,
/// <see cref="PinnedReferenceTable"/> supplies every published figure, and the model does nothing
/// but read the first against the second. Nothing here scores, ranks or predicts — an LLM cannot
/// honestly calibrate a probability, and the component that could have tried is the one that was
/// dropped.
/// </para>
/// <para>
/// Runs as <c>--job trend</c> in the pipeline rather than in the Worker. It is a model call, which
/// is the one thing CLAUDE.md sanctions the pipeline for; the deterministic half runs inside that
/// same job rather than as a Worker poll, so the rule that non-AI background work lives in the
/// Worker is not bent — the Worker still owns the baselines this reads.
/// </para>
/// </remarks>
public class TrendInterpretationService
{
    /// <summary>The version of the brief below, independent of the table it carries.</summary>
    /// <remarks>
    /// 2: the brief now asks for each figure against the published range as well as against their
    /// own usual. The ranges were always in the prompt and nothing told the model to use them, so
    /// a narrative would report five hours of sleep a night without mentioning that seven to nine
    /// is what the NSF recommends at that age — three findings that all said "lower than usual"
    /// and nothing a family could act on.
    /// </remarks>
    internal const int BriefVersion = 2;

    /// <summary>
    /// The brief and the pinned table it carries, as one stamped number. A row written by an older
    /// version is due now, whatever its age — a change to either the wording or the published
    /// figures must reach every member rather than hiding behind the daily cadence.
    /// </summary>
    /// <remarks>
    /// Packed rather than summed. A sum cannot tell the two apart: brief 1 with table 3 and brief
    /// 2 with table 2 both come to 4, so moving between them would leave every stored narrative
    /// looking current and skip the regeneration the version exists to force. Multiplying the
    /// brief by a stride larger than the table will ever reach keeps each combination its own
    /// number, and keeps the stamp monotonic in both so an older row always compares as older.
    /// </remarks>
    internal static int CurrentPromptVersion => (BriefVersion * VersionStride) + PinnedReferenceTable.Version;

    /// <summary>
    /// The room reserved for <see cref="PinnedReferenceTable.Version"/> inside the packed stamp.
    /// A hundred revisions of a table assembled from published guidance is not a near limit; it is
    /// checked rather than assumed all the same, because the failure is silent.
    /// </summary>
    private const int VersionStride = 100;

    /// <summary>
    /// How recently a trend can have been written before the pass skips the member. A day, because
    /// that is the cadence the job runs at; the check exists so a re-run, a retry or a manual
    /// invocation does not pay for a second narrative of the same month.
    /// </summary>
    internal static readonly TimeSpan RegenerationFloor = TimeSpan.FromHours(20);

    /// <summary>The baseline windows a trend is measured against — the long ones the daily rules do not use.</summary>
    private static readonly int[] TrendBaselineWindows = [30, 60, 90];

    /// <summary>
    /// <c>CARDITRACK_TREND_PROMPT</c>. Fixed prefix, as every brief here is: the serving engine can
    /// only reuse a cached prefix that has not changed, and member data always goes after it.
    /// </summary>
    /// <remarks>
    /// No sample sentences. MedGemma repeats phrasing it is shown, and a trend narrative written
    /// from a sample would describe the sample's member rather than this one — the same reason the
    /// digest and journal briefs stopped listing examples (#1098).
    /// </remarks>
    private const string TrendInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        You are reading a month of one person's wearable readings for their family.

        Every figure below was computed from their own measurements before you saw it. Say what the
        figures say. Never work out a percentage, a difference or a direction yourself, and never
        introduce a number that is not in front of you. The one comparison you may make is placing
        a figure against a range printed below it — below it, inside it, or above it — because that
        is reading two given numbers against each other rather than calculating a third.

        You are given two things to measure against, and they answer different questions. Their own
        usual says whether this is a change for them. The published ranges say whether it sits
        where the bodies that publish guidance say it should. Use both: a reading can be down on
        their usual and still comfortably inside the published range, and one can be steady for
        them and outside it. Both are worth a family knowing, and either alone leaves them to
        guess the other.

        Where the published block gives a range for a metric, say where their figure sits against
        it and name the body it comes from. Where the block gives no range for a metric, say
        nothing about a range for it — that absence is deliberate, and there is no figure you may
        supply in its place.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: what has been happening over this stretch, in three or four sentences. Where a
          metric has moved, say which way and roughly how far, in the words the figures use. Where
          a metric has a published range, say where it sits against it — whether or not it has
          moved, because sitting outside guidance while holding perfectly steady is exactly the
          thing a family would otherwise never be told.
        - keyFindings: up to three short lines. Each names one thing worth noticing — a movement
          against their usual, or where a figure sits against published guidance, or both in one
          line where they are the same metric. "Lower than usual" on its own says very little to a
          family; "sleeping about 5 hours a night, below the 7 to 9 recommended at their age" is
          the same finding said usefully. Leave the list empty when nothing has moved and every
          metric that has a published range sits inside it. Metrics with no published range are
          judged on movement alone, since there is nothing for them to sit inside or outside of.

        Never name a condition, a diagnosis or a treatment. Never give a score, a probability, a
        risk level or a prediction of what will happen next. Saying a figure sits outside a
        published range is a fact about the figure and is wanted; saying what it might lead to,
        how likely that is, or what it puts them at risk of is none of those things and must not
        appear. Where the readings have been steady, say so plainly rather than looking for
        something to report.
        """ + MedicalPromptBlocks.ContextGuardrail;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMedicalAiService _medicalAi;
    private readonly MemberContextComposer _memberContext;
    private readonly ILogger<TrendInterpretationService> _logger;
    private readonly TimeProvider _timeProvider;

    public TrendInterpretationService(
        IUnitOfWork unitOfWork,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        ILogger<TrendInterpretationService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The model's answer. Mirrors the baseline insight's shape — same two fields, same guards.</summary>
    public sealed class TrendAiResponse
    {
        public string Summary { get; set; } = string.Empty;
        public List<string> KeyFindings { get; set; } = [];
    }

    /// <summary>
    /// Writes trend narratives for every member with enough history, skipping those already read
    /// today. Returns how many were written.
    /// </summary>
    public async Task<int> InterpretDueMembersAsync(CancellationToken ct = default)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var written = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await InterpretMemberAsync(memberId, utcNow, ct))
                    written++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One member's failure is not the pass's: the next member's month is unaffected by
                // whatever went wrong with this one's.
                _logger.LogError(ex,
                    "Trend interpretation failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Trend pass complete. Narratives written: {Written} of {Candidates} candidate member(s).",
            written, memberIds.Count);

        return written;
    }

    /// <summary>
    /// One member's trend, or false when there is nothing to write: too little history, no
    /// movement worth a model call yet, or a narrative already written by the current brief today.
    /// </summary>
    public async Task<bool> InterpretMemberAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default)
    {
        var existing = await _unitOfWork.MemberInsights.GetByScopeAsync(cardiMemberId, InsightScope.Trend);
        if (existing is not null
            && existing.PromptVersion >= CurrentPromptVersion
            && utcNow - existing.GeneratedAtUtc < RegenerationFloor)
        {
            return false;
        }

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return false;

        // The member's own civil day, not the host's. ActivityLog.Date is the wearer's day, and
        // anchoring to UTC shifts the whole 90-day window for anyone east or west of Greenwich —
        // around local midnight it would drop the day they are currently living and pull in one
        // they are not. The alert and digest passes resolve the same way.
        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, cardiMemberId);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));

        // Ending on the last completed local day, not today. Today's row holds however far
        // through the day the job has run — a morning's steps, a night's sleep and nothing else —
        // and it is the newest point in every moving average and the last point the slope is
        // fitted through, so including it drags the recent end of each series down and reads as a
        // decline that is only the clock. BaselineCalculationWorker ends its window a day back
        // for the same reason, and these deviations are measured against those baselines.
        var through = localToday.AddDays(-1);
        var from = through.AddDays(-(TrendWindowDays - 1));
        var logs = (await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, from, through)).ToList();

        var baselines = new List<PatternBaseline>();
        foreach (var window in TrendBaselineWindows)
        {
            // Sequential, not Task.WhenAll: these run against one DbContext, and EF Core refuses a
            // second operation on a context while one is still running.
            var baseline = await _unitOfWork.PatternBaselines
                .GetLatestByCardiMemberAsync(cardiMemberId, window);
            if (baseline is not null)
                baselines.Add(baseline);
        }

        var features = TrendFeatureCalculator.Compute(logs, baselines, through);
        if (features is null)
        {
            // The cold start the design names: under a month of readings there is no trajectory to
            // narrate, and a line fitted through a new wearer's first fortnight describes them
            // getting used to the watch. This is the learning state, and it says nothing.
            _logger.LogInformation(
                "CardiMember {CardiMemberId} has too little history for a trend narrative.",
                cardiMemberId);
            return false;
        }

        var ageYears = member.DateOfBirth.ToAgeInYears(through);
        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(member, cardiMemberId, through, utcNow, PromptPurpose.Trend), ct);

        var prompt = $"""
            {TrendInstructions}

            [PATIENT CONTEXT]
            {memberContext}

            --- Published reference ranges ---
            {PinnedReferenceTable.For(ageYears)}

            --- Computed trend features ---
            {TrendFeatureCalculator.Render(features)}
            """;

        var reply = await _medicalAi.GenerateStructuredAsync<TrendAiResponse>(prompt, ct);

        var name = NamePlaceholder.FirstName(member.Name);
        var summary = CaregiverFacingTrend(reply.Summary, name);
        if (summary is null)
        {
            // Withheld rather than stored: a narrative the guards emptied, or one that named a
            // condition, is not something to show a family under a heading that says this is how
            // the month went.
            _logger.LogWarning(
                "Trend narrative for CardiMember {CardiMemberId} did not survive the register guards; nothing stored.",
                cardiMemberId);
            return false;
        }

        var findings = reply.KeyFindings
            .Select(finding => CaregiverFacingTrend(finding, name))
            .OfType<string>()
            .Take(InsightLimits.MaxFindings)
            .ToList();

        var row = existing ?? new MemberInsight
        {
            CardiMemberId = cardiMemberId,
            Scope = InsightScope.Trend,
        };

        // Fitted to the column, not trusted to the brief's asked-for length: the completion budget
        // is larger than the column, so a verbose but otherwise valid reply would fail the save
        // and be retried at the same length on every pass.
        row.Summary = InsightLimits.Fit(summary, InsightLimits.Summary)!;
        row.KeyFindings = InsightLimits.JoinFindings(findings);
        row.IsLearning = false;
        row.IsProvisional = false;
        row.BaselinePeriodDays = baselines.Count > 0 ? baselines.Max(b => b.PeriodDays) : null;
        row.GeneratedAtUtc = utcNow;
        row.PromptVersion = CurrentPromptVersion;

        if (existing is null)
            await _unitOfWork.MemberInsights.AddAsync(row);

        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    /// <summary>How many days of readings the features are computed over.</summary>
    internal const int TrendWindowDays = 90;

    /// <summary>
    /// The model's sentence as a caregiver may read it, or null when they may not. The same two
    /// guards every generated line in this product passes: an unresolved name token is worse than
    /// no text, and a named condition is refused outright rather than rewritten — this path has no
    /// rewrite slot to soften one.
    /// </summary>
    private static string? CaregiverFacingTrend(string? text, string? name)
    {
        var resolved = NamePlaceholder.Resolve(text, name);
        if (string.IsNullOrWhiteSpace(resolved) || NamePlaceholder.IsPresentIn(resolved))
            return null;

        return JournalRegisterGuards.NamesACondition(resolved) is null ? resolved.Trim() : null;
    }
}
