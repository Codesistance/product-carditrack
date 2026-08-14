using System.Text.Json;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The real-time assessment pass (docs/llm_design.md): every few minutes, for each member with
/// fresh granular data, decompose the latest hour of heart rate with SSA, and only if that
/// score is a jump (<see cref="DigestRefreshRules.SampleJumpScore"/>) ask the private medical
/// model to assess the denoised picture, store the assessment, and raise an alert when the
/// routed severity says a human should look.
/// <para>
/// The pass runs entirely off the granular store — polled or webhook-fed alike — so it needs
/// nothing from the notification path to function. Deduplication by window start means members
/// whose data has not moved cost no inference. Ordinary windows (score below the jump) are
/// not stored, so a later pass can still consult the model if the same hour later jumps.
/// </para>
/// </summary>
public class RealtimeAssessmentService : IRealtimeAssessmentService
{
    /// <summary>Minutes each assessment window covers — SSA's input, twice its lag window.</summary>
    public const int WindowMinutes = 60;

    /// <summary>
    /// Minimum non-null minutes for a window to be worth assessing. Below 75% coverage the SSA
    /// trend is mostly interpolation and the model would be reading gaps, not the member.
    /// </summary>
    public const int MinCoveredMinutes = 45;

    /// <summary>
    /// How far back the pass looks for the member's latest heart-rate minute. The fetch range
    /// is the freshness bound: data older than this simply is not in the window, so a member
    /// whose device went quiet hours ago yields no assessable minutes — staler data is a
    /// matter for the daily digest, and the silence itself for the inactivity alert.
    /// </summary>
    public const int LookbackHours = 3;

    /// <summary>
    /// Floor for the noise yardstick, bpm. A near-constant window has an RMS near zero, and
    /// dividing by it would turn a 1 bpm wobble into an astronomical deviation score; half a
    /// beat per minute is the smallest jitter worth calling a jitter.
    /// </summary>
    public const double NoiseFloorBpm = 0.5;

    /// <summary>
    /// <c>CARDITRACK_REALTIME_ASSESSMENT_PROMPT</c> — the real-time severity assessment
    /// (docs/llm_design.md prompt registry). The register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>. Exercise / heat / poor-air
    /// illustrations are not listed: MedGemma would echo them. The SSA score threshold stays,
    /// because that is a yardstick, not sample copy. Fixed prefix, cacheable in principle though
    /// not on this model; member data always goes after it.
    /// The closing severity line is the machine-readable part of the answer — the
    /// parser accepts nothing else, so the instruction is explicit about its format.
    /// </summary>
    private const string AssessmentInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        Assess this hour of wearable readings for a family caregiver.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        In the data, trend is the denoised underlying heart rate, and the deviation score says how many typical jitters the latest reading sits from it; scores under 3 are ordinary variation.
        Read activity and conditions in the data before calling a rate unusual.

        Respond with:
        - message: 1-3 plain sentences a caregiver can act on.
        - severity: exactly one of critical, high, medium, or low — critical means seek help
          now, high means look today, medium means worth a mention in the daily summary, low
          means all is well.
        """ + MedicalPromptBlocks.ContextGuardrail;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ISsaDecomposition _ssa;
    private readonly IMedicalAiService _medicalAi;
    private readonly MemberContextComposer _memberContext;
    private readonly IDistributedCache _cache;
    private readonly ILogger<RealtimeAssessmentService> _logger;
    private readonly IAlertNotificationEnqueue? _alertEnqueue;

    public RealtimeAssessmentService(
        IUnitOfWork unitOfWork,
        ISsaDecomposition ssa,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        IDistributedCache cache,
        ILogger<RealtimeAssessmentService> logger,
        IAlertNotificationEnqueue? alertEnqueue = null)
    {
        _unitOfWork = unitOfWork;
        _ssa = ssa;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _cache = cache;
        _logger = logger;
        _alertEnqueue = alertEnqueue;
    }

    public async Task<int> AssessDueMembersAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // Same candidate filter as the digest: active members with recent data. Silence is the
        // inactivity alert's business — an assessment generated from no data would be reassurance
        // from a non-measuring device.
        var since = DateOnly.FromDateTime(utcNow).AddDays(-1);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var assessed = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await AssessMemberAsync(memberId, utcNow, ct))
                    assessed++;
            }
            catch (Exception ex)
            {
                // One member's failure — a model hiccup, a malformed series — must not cost the
                // rest of the fleet this pass; the next scheduled run retries naturally.
                _logger.LogError(ex, "Real-time assessment failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Real-time assessment pass complete. Candidates: {Candidates}, assessments written: {Assessed}.",
            memberIds.Count, assessed);
        return assessed;
    }

    private async Task<bool> AssessMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return false;

        // Whole-hour bounds, per the granular read contract: up through the in-progress hour,
        // back far enough that a full window can end at any minute inside the lookback.
        var rangeEnd = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc)
            .AddHours(1);
        var rangeStart = rangeEnd.AddHours(-LookbackHours - 1);
        var window = await _unitOfWork.GranularMetrics.GetWindowAsync(memberId, rangeStart, rangeEnd, ct);

        if (!window.MinuteSeries.TryGetValue(GranularMetric.HeartRate, out var heartRate))
            return false;

        // The window ends at the member's latest heart-rate minute — assessment follows the
        // data, not the clock, so a device that uploads in bursts is assessed on arrival.
        var lastIndex = Array.FindLastIndex(heartRate, v => v.HasValue);
        if (lastIndex < WindowMinutes - 1)
            return false;

        var windowEnd = rangeStart.AddMinutes(lastIndex + 1);
        var windowStart = windowEnd.AddMinutes(-WindowMinutes);

        // The natural key is the dedup probe: this window only changes when new minutes land,
        // so an existing row means nothing new to assess — and no inference spent.
        if (await _unitOfWork.RealtimeAssessments.ExistsAsync(memberId, windowStart, ct))
            return false;

        var hrWindow = heartRate[(lastIndex - WindowMinutes + 1)..(lastIndex + 1)];
        var covered = hrWindow.Count(v => v.HasValue);
        if (covered < MinCoveredMinutes)
            return false;

        var series = FillGaps(hrWindow);
        var ssa = _ssa.Decompose(series);
        // The floored yardstick is what the deviation score is denominated in, so it is also
        // what the prompt states as the member's typical jitter — the model must see the same
        // units the score uses. The *stored* HrNoiseRms stays the measured residual: flooring
        // it in storage would erase the audit distinction between a genuinely quiet signal and
        // one sitting at the floor.
        var noiseRms = Math.Max(ssa.NoiseRms, NoiseFloorBpm);
        var deviationScore = Math.Abs(series[^1] - ssa.TrendLast) / noiseRms;

        // SSA is the pre-filter that makes a 5-minute cadence affordable: ordinary jitter is
        // not a reason to wake MedGemma, and not storing the row means a later pass can still
        // consult the model if this hour later jumps. The bound is the same number the prompt
        // already tells the model is ordinary variation, and the same number that waives the
        // digest floor — three places, one yardstick.
        //
        // Closing HeartRate alerts still belongs here. The only automatic resolve is "this
        // window is not orange/red"; if we return before that, an episode that has ended
        // stays open and RaiseAlertAsync's cooldown never re-arms.
        if (deviationScore < DigestRefreshRules.SampleJumpScore)
        {
            await ResolveHeartRateAlertsAsync(memberId, utcNow, ct);
            return false;
        }

        var steps = SumIfAny(window.MinuteSeries, GranularMetric.Steps, lastIndex, WindowMinutes);
        var spo2 = MeanIfAny(window.MinuteSeries, GranularMetric.SpO2, lastIndex, WindowMinutes);

        // The member's own context — demographics, the conditions of a recent session, anything a
        // later source adds — is composed centrally now (see MemberContextComposer). The consent
        // gate and the staleness rule that used to live here moved into
        // EnvironmentalContextSource, which keeps this pass's three-hour rule as its own.
        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(
                member, memberId, DateOnly.FromDateTime(utcNow), utcNow, PromptPurpose.RealtimeAssessment),
            ct);

        var prompt = BuildPrompt(memberContext, ssa.TrendLast, deviationScore, noiseRms,
            series[^1], covered, steps, spo2);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<AssessmentAiResponse>(prompt, ct);
        var (rawSeverity, severity) = AssessmentSeverityParser.Map(aiResponse.Severity);

        var assessment = new RealtimeAssessment
        {
            CardiMemberId = memberId,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            HrTrendLast = ssa.TrendLast,
            HrDeviationScore = deviationScore,
            HrNoiseRms = ssa.NoiseRms,
            StepsSum = steps,
            SpO2Mean = spo2,
            ModelOutput = aiResponse.Message.Length <= 4000 ? aiResponse.Message : aiResponse.Message[..4000],
            RawSeverity = rawSeverity,
            Severity = severity,
            SsaEngine = SsaParameters.Engine,
            GeneratedAtUtc = utcNow,
        };
        // The upsert doubles as the concurrency arbiter: the Exists probe above is not atomic
        // with the inference, so two overlapping executions can both reach this line — but only
        // one of them *inserts*, and only the inserter may route an alert. The losing pass has
        // merely duplicated an inference the upsert converges; a pre-inference claim row was
        // rejected because a failed model call would strand the claim and silence the window.
        var inserted = await _unitOfWork.RealtimeAssessments.UpsertAsync(assessment, ct);

        if (severity is null)
        {
            // Fail safe, in both directions: a severity word that did not map to the product
            // taxonomy is stored for audit (RawSeverity) but routes nowhere — the model cannot
            // page a family by deviating from the schema's vocabulary, and the row's null
            // Severity is the visible record that it did.
            _logger.LogWarning(
                "Model's structured severity field for CardiMember {CardiMemberId} did not map to a known severity word.",
                memberId);
        }
        else if (severity >= AlertSeverity.Orange && inserted)
        {
            await RaiseAlertAsync(assessment, ct);
        }
        else if (inserted)
        {
            // A window the model read as ordinary is this member's heart-rate episode ending, and
            // closing it is what re-arms the cooldown in RaiseAlertAsync. Nothing else resolves an
            // alert, so without this one anomaly would silence every later one for good.
            await ResolveHeartRateAlertsAsync(assessment.CardiMemberId, utcNow, ct);
        }

        return true;
    }

    private async Task ResolveHeartRateAlertsAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        // Include soft-deleted rows so a dismissed heart episode is actually closed when the
        // hour comes back ordinary — otherwise deleting the card would re-arm the path while
        // the same anomaly was still running.
        var existing = await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false);
        if (AlertResolution.Resolve(existing, a => a.AlertType == AlertType.HeartRate, utcNow) > 0)
        {
            await _unitOfWork.SaveChangesAsync();

            // The dashboard's live status line may have been generated while this alert was
            // still open, so it can go on describing a tier the member has since left.
            await _cache.RemoveAsync(DashboardStatusCacheKey.For(memberId), ct);
        }
    }

    private async Task RaiseAlertAsync(RealtimeAssessment assessment, CancellationToken ct)
    {
        // Cooldown: one unresolved heart-rate alert at a time, including a card the caregiver
        // already deleted — that dismissed this episode, not the next pass of the same anomaly.
        // Resolving when the hour comes back ordinary (above) is what re-arms it. A sustained
        // anomaly would otherwise re-alert every pass, and twelve pages an hour about one event
        // teaches families to ignore the pager.
        var existing = await _unitOfWork.Alerts.GetByCardiMemberAsync(assessment.CardiMemberId, activeOnly: false);
        if (existing.Any(a => a.AlertType == AlertType.HeartRate && !a.IsResolved))
            return;

        ct.ThrowIfCancellationRequested();
        var alert = new Alert
        {
            CardiMemberId = assessment.CardiMemberId,
            AlertType = AlertType.HeartRate,
            Severity = assessment.Severity!.Value,
            Title = assessment.Severity == AlertSeverity.Red
                ? "Heart rate needs urgent attention"
                : "Heart rate worth checking on",
            Message = assessment.ModelOutput.Length <= 2000
                ? assessment.ModelOutput
                : assessment.ModelOutput[..2000],
            TriggeredDate = assessment.GeneratedAtUtc,
            MetricValues = JsonSerializer.Serialize(new
            {
                rule = AlertRuleMarkers.RealtimeHeartRateRule,
                hrTrendLast = Math.Round(assessment.HrTrendLast, 1),
                hrDeviationScore = Math.Round(assessment.HrDeviationScore, 2),
                hrNoiseRms = Math.Round(assessment.HrNoiseRms, 2),
                windowStartUtc = assessment.WindowStartUtc,
                windowEndUtc = assessment.WindowEndUtc,
            }),
        };
        await _unitOfWork.Alerts.AddAsync(alert);
        await _unitOfWork.SaveChangesAsync();
        await _cache.RemoveAsync(DashboardStatusCacheKey.For(assessment.CardiMemberId), ct);

        // Transport, not a copy of the send stack: the API's internal enqueue endpoint runs
        // the same DispatchService the Worker uses. A failed POST must not roll back the
        // alert — NotificationDispatchWorker cannot see a row that was never written, but it
        // also cannot see one the API never accepted; the next assessor pass is suppressed
        // by the unresolved-alert cooldown, so this is best-effort with a log.
        if (_alertEnqueue is null)
        {
            _logger.LogWarning(
                "Alert {AlertId} was raised with no enqueue transport registered — caregivers will not be pushed.",
                alert.Id);
            return;
        }

        try
        {
            await _alertEnqueue.EnqueueForAlertAsync(alert.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Push enqueue failed for Alert {AlertId}.", alert.Id);
        }
    }

    /// <summary>MedGemma's reply shape for this prompt. Internal, not Application/DTOs — this
    /// describes the private model's reply, not the public API contract; internal rather than
    /// private so IMedicalAiService.GenerateStructuredAsync&lt;T&gt; can be exercised in tests.</summary>
    internal sealed record AssessmentAiResponse
    {
        public required string Message { get; init; }
        public required string Severity { get; init; }
    }

    private static string BuildPrompt(
        string memberContext, double trendLast, double deviationScore,
        double noiseRms, double lastReading, int coveredMinutes, double? steps, double? spo2)
    {
        var contextLines = new List<string>
        {
            $"Denoised heart rate trend, end of hour: {trendLast:F0} bpm",
            $"Latest reading: {lastReading:F0} bpm",
            $"Deviation score (typical jitters from trend): {deviationScore:F1}",
            $"Typical jitter for this member: {noiseRms:F1} bpm",
            $"Minutes with data this hour: {coveredMinutes} of {WindowMinutes}",
            steps.HasValue ? $"Steps this hour: {steps:F0}" : "Steps this hour: not measured",
            spo2.HasValue ? $"Average SpO2 this hour: {spo2:F0}%" : "SpO2 this hour: not measured",
        };

        return $"""
            {AssessmentInstructions}

            {memberContext}

            --- Last hour of data ---
            {string.Join("\n", contextLines)}
            """;
    }

    /// <summary>
    /// The SSA input: the window's minutes with gaps carried forward from the last observation
    /// (and the leading gap, if any, backfilled from the first). Interpolation exists only in
    /// this array — stored minutes stay exactly as measured.
    /// </summary>
    private static double[] FillGaps(float?[] window)
    {
        var series = new double[window.Length];
        var first = Array.FindIndex(window, v => v.HasValue);
        var previous = (double)window[first]!.Value;
        for (var i = 0; i < window.Length; i++)
        {
            if (window[i].HasValue)
                previous = window[i]!.Value;
            series[i] = previous;
        }

        return series;
    }

    private static double? SumIfAny(
        IReadOnlyDictionary<GranularMetric, float?[]> minuteSeries, GranularMetric metric,
        int lastIndex, int windowMinutes)
    {
        var slice = Slice(minuteSeries, metric, lastIndex, windowMinutes);
        if (slice is null || !slice.Any(v => v.HasValue))
            return null;

        return slice.Where(v => v.HasValue).Sum(v => (double)v!.Value);
    }

    private static double? MeanIfAny(
        IReadOnlyDictionary<GranularMetric, float?[]> minuteSeries, GranularMetric metric,
        int lastIndex, int windowMinutes)
    {
        var slice = Slice(minuteSeries, metric, lastIndex, windowMinutes);
        if (slice is null || !slice.Any(v => v.HasValue))
            return null;

        return slice.Where(v => v.HasValue).Average(v => (double)v!.Value);
    }

    private static float?[]? Slice(
        IReadOnlyDictionary<GranularMetric, float?[]> minuteSeries, GranularMetric metric,
        int lastIndex, int windowMinutes)
    {
        if (!minuteSeries.TryGetValue(metric, out var values) || values.Length <= lastIndex)
            return null;

        return values[(lastIndex - windowMinutes + 1)..(lastIndex + 1)];
    }
}
