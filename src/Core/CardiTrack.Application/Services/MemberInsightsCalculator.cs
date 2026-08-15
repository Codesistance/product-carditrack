using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Pure, stateless derivations from a member's activity logs/baseline/alerts/sync state — the Key
/// Metrics cards, the overall clinical severity, and the data-pipeline freshness tier. Extracted
/// out of <see cref="DashboardService"/> so <see cref="CardiMemberService"/> (the CardiMember
/// Detail screen's trend sparklines and severity accent) can call the same logic instead of a
/// second copy drifting from it.
/// </summary>
public static class MemberInsightsCalculator
{
    // Deviation-from-baseline thresholds for per-metric status colouring; consistent with
    // the "medium" alert sensitivity in docs/execution/backend/api/alerts.md.
    private const decimal YellowDeviationPercent = 30m;
    private const decimal OrangeDeviationPercent = 50m;

    /// <summary>
    /// Days of daily history carried in every metric's <see cref="DashboardMetric.Series"/>, sized
    /// to the widest window the CardiMember Detail screen's trend cards offer (7 / 14 / 30 days) so
    /// switching between them is a client-side slice rather than another round trip. Both callers
    /// already read this many days of logs for the baseline progress, so the wider series costs no
    /// extra query.
    /// </summary>
    public const int SeriesDays = 30;

    /// <summary>Stars a Key Metrics card rates a reading out of.</summary>
    public const int QualityScoreMax = 5;

    /// <summary>No data for this long or longer reads as an outright gap, not just "a bit stale".</summary>
    public const int RedStaleHours = 12;

    /// <summary>No data for this long or longer is worth a caregiver's attention, short of a gap.</summary>
    public const int AmberStaleHours = 4;

    /// <summary>
    /// Builds the Key Metrics cards from a member's daily history.
    /// </summary>
    /// <param name="ageYears">
    /// The member's age, for the one published reference range that is split by it — see
    /// <see cref="HealthReferenceRanges.Sleep"/>. Required rather than optional because both
    /// callers hold the member's date of birth already, and a default would quietly draw every
    /// older adult the younger band.
    /// </param>
    /// <remarks>
    /// Each metric is resolved independently, down the days newest-first, rather than all of them
    /// reading a single "latest row". Ingestion stores the day in progress, so today's row appears
    /// as soon as the provider reports anything at all — and a row carrying steps but not yet a
    /// resting heart rate would blank the cards that were populated a moment ago if they all had
    /// to come from the same day. This is the same coalescing rule <see cref="ActivityLogMerge"/>
    /// applies across a member's devices, applied across days.
    /// </remarks>
    public static DashboardMetrics BuildMetrics(
        List<ActivityLog> logs, PatternBaseline? baseline, DateOnly today, int ageYears)
    {
        var byDate = logs
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());
        var newestFirst = byDate.OrderByDescending(entry => entry.Key).Select(entry => entry.Value).ToList();

        var latestSteps = LatestWith(newestFirst, l => l.Steps);
        var steps = BuildMetric(
            value: latestSteps?.Steps,
            baselineValue: baseline?.AvgSteps,
            unit: "steps",
            series: BuildSeries(byDate, today, l => l.Steps),
            // Steps accumulate through the day, so a day still in progress has nothing to compare
            // against a whole-day average — at breakfast every member alive would read as a
            // catastrophic drop. The dashboard bar compares today to yesterday's total instead,
            // which is honest about a running count without scoring it against their usual day.
            comparable: latestSteps?.Date != today);
        // This member's own usual day and nothing else. It used to fall back to a flat 10,000 for
        // a member with no baseline yet, which is a pedometer brand's name from 1965 rather than
        // anybody's guidance — the same invention HealthReferenceRanges refuses to make for steps,
        // made here instead. A member still being learned now gets no usual-day figure; the
        // dashboard bar does not need one, because it fills against the previous calendar day.
        steps.Goal = baseline?.AvgSteps;
        // Direction counts here: a member who walked half again as far as usual has not earned a
        // worse rating for it, so only a shortfall costs stars. Null for a day still in progress
        // for the same reason ChangePercent is — the bar compares that running total to yesterday
        // rather than rating it against a finished usual day.
        steps.QualityScore = RateAgainstNormal(steps.ChangePercent, shortfallOnly: true);

        // Resting heart rate and sleep are daily summary values, not running totals: the provider
        // either has one for the day or does not. A today reading is a whole reading, so unlike
        // steps it stays comparable against the baseline.
        var latestHeartRate = LatestWith(newestFirst, l => l.RestingHeartRate);
        var heartRate = BuildMetric(
            value: latestHeartRate?.RestingHeartRate,
            baselineValue: baseline?.AvgRestingHeartRate,
            unit: "bpm",
            series: BuildSeries(byDate, today, l => l.RestingHeartRate),
            reference: HealthReferenceRanges.RestingHeartRate);
        if (baseline?.AvgRestingHeartRate is int avgHr && baseline.StdDevHeartRate is decimal stdHr)
        {
            heartRate.RangeLow = (int)Math.Round(avgHr - stdHr, MidpointRounding.AwayFromZero);
            heartRate.RangeHigh = (int)Math.Round(avgHr + stdHr, MidpointRounding.AwayFromZero);
        }
        // Both directions count for a resting heart rate — unusually low is as much a departure
        // from this member's own normal as unusually high, so neither is spared the rating.
        heartRate.QualityScore = RateAgainstNormal(heartRate.ChangePercent);

        var latestSleep = LatestWith(newestFirst, l => l.SleepMinutes);
        var sleep = BuildMetric(
            value: latestSleep?.SleepMinutes is int sm ? Math.Round(sm / 60m, 1) : null,
            baselineValue: baseline?.AvgSleepMinutes is int abm ? Math.Round(abm / 60m, 1) : null,
            unit: "hours",
            series: BuildSeries(byDate, today, l => l.SleepMinutes is int m ? Math.Round(m / 60m, 1) : (decimal?)null),
            // The only one of the four published ranges that is split by age; the rest are
            // published as single adult bands, which is all a CardiMember can be.
            reference: HealthReferenceRanges.Sleep(ageYears));
        // A night is rated on both of the things that can be wrong with it, taking the worse:
        // how well it was slept, and how much of it there was. Read off the same night as the
        // duration above, so the stars can never describe the quality of one night next to the
        // length of another.
        //
        // How well: sleep efficiency, the share of the time in bed actually spent asleep. Plenty
        // of wearables report a duration but no efficiency at all, which leaves this half unrated
        // rather than dropping the rating entirely.
        var sleptWell = latestSleep?.SleepEfficiency switch
        {
            >= 90 => 5,
            >= 80 => 4,
            >= 70 => 3,
            >= 60 => 2,
            not null => 1,
            null => (int?)null,
        };
        // How much: the length of the night against this member's own normal. A shorter night than
        // usual is the reading being looked for, and like steps a longer one is not marked down.
        // The cap takes the card's own Reference — the same band the chart draws — so the rating
        // and the band a caregiver reads it against cannot drift apart.
        sleep.QualityScore = CapAtRecommendedSleep(
            Lower(sleptWell, RateAgainstNormal(sleep.ChangePercent, shortfallOnly: true)),
            latestSleep?.SleepMinutes,
            sleep.Reference);

        // Temperature carries its own per-day, device-derived baseline (Google Health computes
        // it, not our BaselineCalculationWorker), so it compares against that rather than
        // PatternBaseline — meaningful even during the 30-day learning window.
        var latestTemp = LatestWith(newestFirst, l => l.Temperature);
        var temperature = BuildMetric(
            value: latestTemp?.Temperature,
            baselineValue: latestTemp?.TemperatureBaseline,
            unit: "°C",
            series: BuildSeries(byDate, today, l => l.Temperature));
        // Percent deviation is the wrong comparison unit here — a clinically meaningful ~1°C
        // shift on a ~33-37°C baseline is only 2-3%, which never crosses the shared 30%/50%
        // thresholds BuildMetric just applied. Compare against the device's own per-day stddev
        // (TemperatureVariation) instead, same shape as resting heart rate's RangeLow/RangeHigh.
        if (latestTemp?.Temperature is { } tempValue
            && latestTemp.TemperatureBaseline is { } tempBaseline
            && latestTemp.TemperatureVariation is > 0m and { } tempVariation)
        {
            var deviation = Math.Abs(tempValue - tempBaseline) / tempVariation;
            temperature.Status = deviation switch
            {
                <= 1m => "green",
                <= 2m => "yellow",
                _ => "orange",
            };
            // The same evidence as the status just above, one band finer, so the stars and the
            // pill on this card can never disagree: 4-5 stars is green, 3-2 yellow, 1 orange.
            temperature.QualityScore = deviation switch
            {
                <= 0.5m => 5,
                <= 1m => 4,
                <= 1.5m => 3,
                <= 2m => 2,
                _ => 1,
            };
        }

        // No baseline concept exists for SpO2 yet — shown as a plain reading, not a trend, and
        // with no star rating either: there is nothing to rate it against but an invented normal.
        // The published reference range is the one comparison these two metrics do have, and it is
        // background for a chart rather than a judgement, so it neither colours the status nor
        // earns them a rating.
        var latestSpO2 = LatestWith(newestFirst, l => l.SpO2Average);
        var spO2 = BuildMetric(
            value: latestSpO2?.SpO2Average,
            baselineValue: null,
            unit: "%",
            series: BuildSeries(byDate, today, l => l.SpO2Average),
            reference: HealthReferenceRanges.SpO2);

        // Breathing rate has no established-baseline concept yet either, same as SpO2 above.
        var latestBreathing = LatestWith(newestFirst, l => l.BreathingRate);
        var breathingRate = BuildMetric(
            value: latestBreathing?.BreathingRate,
            baselineValue: null,
            unit: "brpm",
            series: BuildSeries(byDate, today, l => l.BreathingRate),
            reference: HealthReferenceRanges.BreathingRate);

        return new DashboardMetrics
        {
            Steps = steps,
            RestingHeartRate = heartRate,
            Sleep = sleep,
            Temperature = temperature,
            SpO2 = spO2,
            BreathingRate = breathingRate,
        };
    }

    /// <summary>
    /// Rates a reading against this member's own normal out of <see cref="QualityScoreMax"/>, from
    /// exactly the evidence <see cref="BuildMetric"/> already colours the status with — five stars
    /// is "on their normal", one is "a long way off it". The bands nest inside the status
    /// thresholds, so the stars and the status on a card can never contradict each other: 3-5
    /// stars is green, 2 is yellow, 1 is orange. Null whenever there is no comparison to make,
    /// which leaves the card's star row hidden rather than rating a number against nothing.
    /// </summary>
    /// <param name="shortfallOnly">
    /// True for metrics where overshooting the normal is not a departure worth marking down —
    /// steps and sleep duration. Those rate on the shortfall alone, matching the direction the
    /// dashboard's own trend arrow already reads them in. Note that for sleep this earns top marks
    /// for a long-enough night only in this member's terms; <see cref="CapAtRecommendedSleep"/>
    /// still has to agree the night was long enough at all.
    /// </param>
    private static int? RateAgainstNormal(decimal? changePercent, bool shortfallOnly = false)
    {
        if (changePercent is not { } change)
            return null;
        if (shortfallOnly && change >= 0)
            return QualityScoreMax;

        return Math.Abs(change) switch
        {
            <= 5m => 5,
            <= 15m => 4,
            <= YellowDeviationPercent => 3,
            <= OrangeDeviationPercent => 2,
            _ => 1,
        };
    }

    /// <summary>
    /// Holds a sleep rating down to what the length of the night can support, against the published
    /// recommended band for a member of this age (<see cref="HealthReferenceRanges.Sleep"/>) — one
    /// star for each hour outside it, so 4.5 hours cannot be rated above two however well those
    /// hours were slept, and neither can 12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sleep is the one metric where the member's own normal cannot be the whole of the rating.
    /// Both of the comparisons it is otherwise made against are blind to how long the night was:
    /// efficiency is a ratio, so 4.4 hours asleep out of 4.5 in bed is 98% and five stars for
    /// a night nowhere near long enough; and a member who habitually sleeps 4.5 hours has a
    /// baseline that says a 4.5-hour night is exactly normal — the very reading a caregiver is
    /// watching for, rated top marks because it keeps happening.
    /// </para>
    /// <para>
    /// Both ends of the band, because too long is a departure the same way too short is, and the
    /// member's own normal cannot catch it either: the duration comparison this cap sits on top of
    /// is <c>shortfallOnly</c>, which reads every overshoot as five stars. That asymmetry is right
    /// where it is — a member catching up after a bad week has not earned a worse rating for it —
    /// but it means a night far beyond the recommendation would otherwise go unremarked, and a jump
    /// from seven hours to twelve is exactly what someone is watching for. Only the published band
    /// can see it, because "too long" has no meaning except in absolute terms.
    /// </para>
    /// <para>
    /// This is the single, deliberate exception to <see cref="MetricReference"/> being
    /// presentational only, and it is written to stay inside that rule's intent: the recommendation
    /// can only ever lower a rating the member's own data already earned — never raise one, and
    /// never create one where there was nothing to rate. An unusual night is still reported as an
    /// unusual night, not named a disorder — CardiTrack is not a medical device — but it will not
    /// be applauded either.
    /// </para>
    /// </remarks>
    /// <param name="sleepMinutes">
    /// The night as it was measured, not as the card rounds it. <see cref="DashboardMetric.Value"/>
    /// carries hours to one decimal place, which is the right resolution to read but the wrong one
    /// to threshold on: 418 minutes is 6.97 hours and rounds to 7.0, clearing a floor it is three
    /// minutes short of.
    /// </param>
    /// <param name="recommended">
    /// The sleep card's own <see cref="DashboardMetric.Reference"/> — the very band the chart
    /// draws, passed rather than re-derived so the rating and the band a caregiver reads it
    /// against cannot drift apart by construction. Only the ceiling is age-split: the NSF drops
    /// from 9 hours to 8 at <see cref="HealthReferenceRanges.OlderAdultAge"/>, and publishes the
    /// same 7-hour floor either side. Null-tolerant for symmetry with the other guards, though
    /// the sleep card always carries one.
    /// </param>
    private static int? CapAtRecommendedSleep(int? score, int? sleepMinutes, MetricReference? recommended)
    {
        if (score is null || sleepMinutes is not { } minutes || recommended is null)
            return score;

        var hours = minutes / 60m;
        var hoursOutside =
            hours < recommended.Low ? recommended.Low - hours
            : hours > recommended.High ? hours - recommended.High
            : 0m;

        return Math.Min(score.Value, hoursOutside switch
        {
            <= 0m => QualityScoreMax,
            <= 1m => 4,
            <= 2m => 3,
            <= 3m => 2,
            _ => 1,
        });
    }

    /// <summary>
    /// The worse of two ratings of the same reading, skipping either that could not be made at all
    /// — so a metric rated on two things falls back to whichever one its data supports, and is
    /// unrated only when neither could be made.
    /// </summary>
    private static int? Lower(int? left, int? right) =>
        left is null ? right
        : right is null ? left
        : Math.Min(left.Value, right.Value);

    /// <summary>The most recent day that actually reported this metric, or null when none did.</summary>
    private static ActivityLog? LatestWith<T>(IReadOnlyList<ActivityLog> newestFirst, Func<ActivityLog, T?> select)
        where T : struct =>
        newestFirst.FirstOrDefault(log => select(log).HasValue);

    /// <param name="comparable">
    /// False when the reading covers a period that is not over yet, which makes a
    /// baseline comparison meaningless rather than merely uncertain. Leaves both
    /// <see cref="DashboardMetric.ChangePercent"/> and the derived status unset, so the client
    /// falls back to the card's plain presentation instead of colouring a number it cannot judge.
    /// </param>
    /// <param name="reference">
    /// The published typical-adult range to draw behind the series, or null for a metric no
    /// standards body publishes one for — see <see cref="HealthReferenceRanges"/>. Presentational
    /// only: the status below stays relative to this member's own baseline.
    /// </param>
    private static DashboardMetric BuildMetric(
        decimal? value,
        decimal? baselineValue,
        string unit,
        List<MetricPoint> series,
        bool comparable = true,
        MetricReference? reference = null)
    {
        decimal? changePercent = null;
        if (comparable && value is not null && baselineValue is > 0)
            changePercent = Math.Round((value.Value - baselineValue.Value) / baselineValue.Value * 100m, 1);

        return new DashboardMetric
        {
            Value = value,
            Baseline = baselineValue,
            ChangePercent = changePercent,
            Unit = unit,
            Status = changePercent is null
                ? "unknown"
                : Math.Abs(changePercent.Value) switch
                {
                    <= YellowDeviationPercent => "green",
                    <= OrangeDeviationPercent => "yellow",
                    _ => "orange",
                },
            Series = series,
            Reference = reference,
        };
    }

    private static List<MetricPoint> BuildSeries(
        Dictionary<DateOnly, ActivityLog> byDate, DateOnly today, Func<ActivityLog, decimal?> selector)
    {
        var series = new List<MetricPoint>(SeriesDays);
        for (var offset = SeriesDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            series.Add(new MetricPoint
            {
                Date = date,
                Value = byDate.TryGetValue(date, out var log) ? selector(log) : null,
            });
        }
        return series;
    }

    public static string ComputeHealthStatus(
        IReadOnlyCollection<Alert> unresolvedAlerts, bool isLearning, DashboardMetrics? metrics)
    {
        if (unresolvedAlerts.Count > 0)
        {
            var worst = unresolvedAlerts.Max(a => a.Severity);
            if (worst >= AlertSeverity.Yellow)
                return SeverityLabel(worst);
        }
        return isLearning || metrics is null ? "unknown" : "green";
    }

    public static string SeverityLabel(AlertSeverity severity) =>
        severity.ToString().ToLowerInvariant();

    /// <summary>
    /// Deterministic data-pipeline freshness, independent of <see cref="ComputeHealthStatus"/>'s
    /// clinical severity: red/amber describe a data gap (no sync recently), blue/green describe
    /// whether the most recent sync has actually been assessed yet. Never non-deterministic —
    /// unlike the rotating flavour copy this replaced, the same inputs always produce the same
    /// tier and message.
    /// </summary>
    public static (string Tier, string Message) ComputeDataFreshness(
        DateTime? lastSyncedAt, DateTime? lastAssessedAt, DateTime now, string firstName)
    {
        if (lastSyncedAt is not { } synced)
            return ("red", $"No data from {firstName} yet");

        var staleFor = now - synced;
        if (staleFor >= TimeSpan.FromHours(RedStaleHours))
            return ("red", $"No data from {firstName} in over {RedStaleHours} hours");
        if (staleFor >= TimeSpan.FromHours(AmberStaleHours))
            return ("amber", $"No data from {firstName} in over {AmberStaleHours} hours");

        // "Processed" means an assessment covers data at least as recent as the last sync — not
        // just that *an* assessment exists.
        var isProcessed = lastAssessedAt is { } assessed && assessed >= synced;
        return isProcessed ? ("green", "Data processed") : ("blue", "Data updated");
    }
}
