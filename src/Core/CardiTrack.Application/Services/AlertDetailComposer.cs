using System.Globalization;
using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services;

/// <summary>
/// The two stretches an elapsed-matched comparison covers, and the member's local today they were
/// derived from. All four bounds are whole UTC hours, because the comparison is served from the
/// hourly rollup ladder rather than by unpacking minute vectors.
/// </summary>
/// <param name="Today">Local today — the last day the chart plots, and the partial one.</param>
/// <param name="TodayFromUtc">The hour local midnight fell in, today.</param>
/// <param name="TodayToUtc">The top of the current hour — exclusive end.</param>
/// <param name="ComparisonFromUtc">The same, yesterday.</param>
/// <param name="ComparisonToUtc">Exclusive end of the identical run of hours, yesterday.</param>
/// <param name="Hours">How many whole hours each stretch covers. Equal by construction.</param>
public readonly record struct ElapsedMatch(
    DateOnly Today,
    DateTime TodayFromUtc,
    DateTime TodayToUtc,
    DateTime ComparisonFromUtc,
    DateTime ComparisonToUtc,
    int Hours);

/// <summary>Both halves of the elapsed match, already summed. The comparison half may be absent.</summary>
public sealed record ElapsedSteps(decimal? Today, decimal? Comparison);

/// <summary>
/// Pure mapping from one alert plus the logs/series that belong to it onto
/// <see cref="AlertDetailResponse"/>. I/O stays in <see cref="AlertService"/> so this can name
/// which window to fetch — and so a sleep alert cannot accidentally be handed a steps chart.
/// </summary>
public static class AlertDetailComposer
{
    public const int ActivityDays = 14;
    public const int TrendDays = 28;
    public const int HeartRateDays = 7;
    public const int SleepDays = 14;

    /// <summary>Cap on minute-grain points so a long realtime window still plots as a line.</summary>
    public const int GranularMaxPoints = 90;

    public const string DeviceSilenceRule = "device_silence";
    public const string RealtimeHeartRateRule = "realtime_hr";

    /// <summary>The producer stamp, or null when the JSON is missing or unreadable.</summary>
    public static string? ReadRule(string? metricValues)
    {
        if (string.IsNullOrWhiteSpace(metricValues))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metricValues);
            return doc.RootElement.TryGetProperty("rule", out var rule)
                ? rule.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How many calendar days of activity logs this rule's chart needs, or 0 when the rule has
    /// no daily series (silence, or the sub-daily heart-rate window).
    /// </summary>
    public static int DailyLogDays(string? rule) => rule switch
    {
        StatisticalAlertRules.LongTermTrendRule => TrendDays,
        StatisticalAlertRules.ElevatedHeartRateRule => HeartRateDays,
        StatisticalAlertRules.IrregularSleepRule => SleepDays,
        StatisticalAlertRules.ActivityDeclineRule => ActivityDays,
        StatisticalAlertRules.NoMorningActivityRule => ActivityDays,
        StatisticalAlertRules.HeartRateVariabilityDropRule => HeartRateDays,
        StatisticalAlertRules.OvernightBreathingUpRule => HeartRateDays,
        StatisticalAlertRules.ElevatedZoneWithoutMovementRule => ActivityDays,
        StatisticalAlertRules.DaytimeInactivityBlockRule => ActivityDays,
        // The published-range rules read three weeks; the chart shows the stretch around the
        // finding, at the grain each metric's own chart already uses.
        StatisticalAlertRules.SleepOutsideRangeRule => SleepDays,
        StatisticalAlertRules.RestingHeartRateOutsideRangeRule => SleepDays,
        StatisticalAlertRules.OxygenBelowRangeRule => SleepDays,
        DeviceSilenceRule => 0,
        RealtimeHeartRateRule => 0,

        // No daily series, and deliberately not the default below. A rhythm finding is an event
        // the device stamped, not a reading with a trend behind it — and the fallback would put a
        // fortnight of step counts under "an ECG came back as atrial fibrillation", which reads as
        // evidence for a finding that has nothing to do with steps.
        StatisticalAlertRules.EcgAtrialFibrillationRule => 0,
        StatisticalAlertRules.IrregularRhythmRule => 0,
        // A markerless Inactivity row is the old device-silence producer; don't fetch steps.
        null => 0,
        _ => ActivityDays,
    };

    public static bool NeedsGranular(string? rule) => rule == RealtimeHeartRateRule;

    /// <summary>
    /// Whether this rule's chart runs up to the day in progress and therefore needs the elapsed
    /// match — the step charts, and only those.
    /// </summary>
    /// <remarks>
    /// Heart rate and sleep are left out deliberately rather than forgotten. A resting heart rate
    /// and a night's sleep are settled figures by the time they are reported; the calendar day
    /// they are filed under having hours left in it does not make them running totals. Steps are
    /// the metric that genuinely accumulates through the day, and the only one where plotting the
    /// day in progress beside finished ones misleads.
    /// </remarks>
    public static bool NeedsElapsedMatch(string? rule) => rule is
        StatisticalAlertRules.ActivityDeclineRule
        or StatisticalAlertRules.NoMorningActivityRule
        or StatisticalAlertRules.LongTermTrendRule;

    /// <summary>
    /// Rules that judge yesterday's completed day and fire the next local day. Their
    /// <see cref="AlertDetailResponse.AboutDate"/> is the day judged, not the firing day —
    /// otherwise the list buckets them under Today and the banner dates the quieter day as
    /// this afternoon.
    /// </summary>
    /// <remarks>
    /// Night-ending rules (<c>irregular_sleep</c>, <c>hrv_drop</c>, <c>overnight_breathing_up</c>)
    /// are not here: those stamp the civil day the night ended on, which is often the firing
    /// day. Falling back to the day before would move a morning sleep card onto the night
    /// before last.
    /// </remarks>
    public static bool IsAboutPreviousLocalDay(string? rule) => rule is
        StatisticalAlertRules.ActivityDeclineRule
        or StatisticalAlertRules.ElevatedHeartRateRule
        or StatisticalAlertRules.LongTermTrendRule
        or StatisticalAlertRules.DaytimeInactivityBlockRule
        or StatisticalAlertRules.ElevatedZoneWithoutMovementRule
        or StatisticalAlertRules.RestingHeartRateOutsideRangeRule;

    /// <summary>
    /// The civil day this alert is about. Prefers the <c>day</c> / <c>night</c> stamp the
    /// producer wrote; unstamped yesterday-grain rows (raised before that stamp existed)
    /// fall back to the local day before they fired.
    /// </summary>
    public static DateOnly AboutDate(string? rule, string? metricValues, DateOnly firedOn)
    {
        if (TryParse(metricValues, out var metrics))
        {
            var stamped = ReadDateOnly(metrics, "day") ?? ReadDateOnly(metrics, "night");
            if (stamped is { } day)
                return day;
        }

        return IsAboutPreviousLocalDay(rule) ? firedOn.AddDays(-1) : firedOn;
    }

    /// <summary>
    /// The least of the elapsed hours each day must have data for before the two are worth
    /// comparing. A day the watch spent four hours off the wrist is not the same stretch as a day
    /// it was worn throughout, and reporting one against the other as a like-for-like match would
    /// reintroduce the unfairness this whole comparison exists to remove — just with different
    /// numbers.
    /// </summary>
    public const double MinimumElapsedCoverage = 0.75;

    /// <summary>
    /// The two stretches an elapsed match compares: local midnight to the top of the current hour,
    /// and the identical run of hours the day before. Null before the first full local hour of the
    /// day, where there is nothing whole to compare yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whole hours, because these are served from <c>MetricRollupsHourly</c> — the horizon built
    /// for exactly this question. Reading it out of minute vectors instead meant pulling every
    /// metric's full minute grid for two ~25-hour spans on an endpoint the detail page re-polls
    /// while it is open, to use one of them.
    /// </para>
    /// <para>
    /// Each day's midnight is converted separately rather than by subtracting 24 hours, and the
    /// hour count is taken from today's real elapsed span, so a clock-change day compares two runs
    /// of the same length instead of one that silently gained or lost an hour. In a zone whose
    /// offset is not a whole number of hours the floor shifts both days identically, so the two
    /// stretches stay matched.
    /// </para>
    /// </remarks>
    public static ElapsedMatch? ElapsedMatchFor(DateTime nowUtc, TimeZoneInfo zone)
    {
        var utc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);

        var todayStart = FloorHour(LocalMidnightUtc(nowLocal.Date, zone));
        var todayEnd = FloorHour(utc);

        var hours = (int)(todayEnd - todayStart).TotalHours;
        if (hours <= 0)
            return null;

        var comparisonStart = FloorHour(LocalMidnightUtc(nowLocal.Date.AddDays(-1), zone));

        return new ElapsedMatch(
            DateOnly.FromDateTime(nowLocal),
            todayStart,
            todayEnd,
            comparisonStart,
            comparisonStart.AddHours(hours),
            hours);
    }

    /// <summary>
    /// Steps over a run of hourly rollups, and how many of those hours actually carry samples.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricRollupHourly.Sum"/> is the right column here: steps are counted per minute
    /// and additive, so an hour's sum is the steps taken in it. The hour count is the coverage
    /// signal <see cref="ElapsedStepsFrom"/> gates on.
    /// </remarks>
    public static (decimal Steps, int CoveredHours) StepsOver(IReadOnlyList<MetricRollupHourly> rollups)
    {
        decimal steps = 0;
        var covered = 0;

        foreach (var hour in rollups)
        {
            if (hour.Metric != GranularMetric.Steps || hour.SampleCount <= 0)
                continue;
            steps += (decimal)hour.Sum;
            covered++;
        }

        return (steps, covered);
    }

    /// <summary>
    /// Today-so-far and the matching stretch of the comparison day, or null when today's own hours
    /// are too sparse to report at all. The comparison half is dropped — leaving today's figure to
    /// stand alone — when either day covers less than
    /// <see cref="MinimumElapsedCoverage"/> of <paramref name="elapsedHours"/>.
    /// </summary>
    public static ElapsedSteps? ElapsedStepsFrom(
        IReadOnlyList<MetricRollupHourly> today,
        IReadOnlyList<MetricRollupHourly> comparison,
        int elapsedHours)
    {
        if (elapsedHours <= 0)
            return null;

        var (todaySteps, todayHours) = StepsOver(today);
        if (todayHours == 0)
            return null;

        var floor = elapsedHours * MinimumElapsedCoverage;
        if (todayHours < floor)
            return null;

        var (comparisonSteps, comparisonHours) = StepsOver(comparison);
        return new ElapsedSteps(
            todaySteps,
            comparisonHours >= floor ? comparisonSteps : null);
    }

    /// <summary>
    /// Whole-hour bounds for the granular heart-rate window, or null when the JSON does not
    /// name one. Both ends are aligned to UTC hours, which the granular store requires.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc)? GranularBounds(string? metricValues)
    {
        if (!TryParse(metricValues, out var root))
            return null;

        var start = ReadDateTime(root, "windowStartUtc");
        var end = ReadDateTime(root, "windowEndUtc");
        if (start is null || end is null || end <= start)
            return null;

        var from = FloorHour(start.Value);
        var to = CeilHour(end.Value);
        return to > from ? (from, to) : null;
    }

    /// <param name="elapsedSteps">
    /// Today-so-far and the matching stretch of yesterday, or null when this rule does not need an
    /// elapsed match or the minute store could not answer for it.
    /// </param>
    /// <param name="firedOn">
    /// The member's local calendar day the alert was raised on. When omitted, the UTC date of
    /// <see cref="Alert.TriggeredDate"/> is used — fine for tests, and the fallback the list
    /// mapping uses when it has not resolved the member's zone.
    /// </param>
    public static AlertDetailResponse Compose(
        Alert alert,
        CardiMember? member,
        User? acknowledger,
        IReadOnlyList<ActivityLog> logs,
        DateOnly today,
        GranularWindow? granular,
        PatternBaseline? baseline,
        ElapsedSteps? elapsedSteps = null,
        DateOnly? firedOn = null,
        string? photoUrl = null,
        TimeZoneInfo? timeZone = null)
    {
        var rule = ReadRule(alert.MetricValues);
        TryParse(alert.MetricValues, out var metrics);
        var raisedOn = firedOn ?? DateOnly.FromDateTime(AsUtc(alert.TriggeredDate));
        var aboutDate = AboutDate(rule, alert.MetricValues, raisedOn);
        var stretchStartedAt = ReadDateTime(metrics, "startedAtUtc");
        var bedtime = LocalizedBedtime(baseline?.TypicalBedtime, stretchStartedAt, timeZone);
        var stretchStartLocal = stretchStartedAt is { } started
            ? BaselineClock.Local(started, timeZone)
            : null;

        return new AlertDetailResponse
        {
            Reason = Reason(rule, alert.AlertType),
            AlertId = alert.Id,
            CardiMemberId = alert.CardiMemberId,
            CardiMemberName = member?.Name ?? string.Empty,
            CardiMemberPhotoUrl = photoUrl,
            Phone = member?.Phone,
            EmergencyContactPhone = member?.EmergencyContactPhone,
            EmergencyContactName = member?.EmergencyContactName,
            Type = alert.AlertType.GetDisplayName(),
            Rule = rule,
            Severity = alert.Severity.ToString().ToLowerInvariant(),
            Status = StatusLabel(alert),
            Title = alert.Title,
            Message = alert.Message,
            TriggeredAt = alert.TriggeredDate,
            AboutDate = aboutDate,
            AcknowledgedAt = alert.AcknowledgedDate,
            AcknowledgedByUserId = alert.AcknowledgedByUserId,
            AcknowledgedByName = acknowledger?.Name,
            Comparison = Comparison(rule, metrics, baseline, today, aboutDate, alert.TriggeredDate),
            Evidence = AlertEvidenceComposer.Compose(rule, metrics, baseline),
            Chart = Chart(rule, logs, today, granular, baseline, metrics, member, elapsedSteps, aboutDate),
            LastActivityOn = LastMeasuredStepsDay(logs),
            TypicalWakeTime = ReadString(metrics, "typicalWakeTime")
                ?? baseline?.TypicalWakeTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            TypicalBedtime = bedtime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            StillStretchAsk = stretchStartLocal is { } start
                ? StillStretchAsk(start, bedtime)
                : null,
            StretchStartedLabel = stretchStartLocal?.ToString("h:mm tt", CultureInfo.InvariantCulture),
            LastDataAt = ReadDateTime(metrics, "lastDataUtc"),
            StretchStartedAt = stretchStartedAt,
        };
    }

    /// <summary>
    /// The stored UTC bedtime face, read on the member's wall clock and anchored to the
    /// stretch (or, without one, left as stored). Without a zone the face is unchanged, so
    /// fixtures that already speak in local hours keep working — the same fallback
    /// <see cref="BaselineClock.Local(TimeOnly?, DateOnly, TimeZoneInfo?)"/> documents.
    /// </summary>
    private static TimeOnly? LocalizedBedtime(
        TimeOnly? utcBedtime, DateTime? stretchStartedAtUtc, TimeZoneInfo? timeZone)
    {
        if (utcBedtime is null)
            return null;
        if (timeZone is null || stretchStartedAtUtc is not { } startedAt)
            return utcBedtime;

        return BaselineClock.Local(
            utcBedtime, DateOnly.FromDateTime(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)), timeZone);
    }

    /// <summary>
    /// What the alert is about, for the detail screen's icon. The producer's rule answers first
    /// because it is the specific fact; <see cref="AlertType"/> is the fallback for rows written
    /// before rule markers existed, and it is a coarser question — <c>PatternBreak</c> names no
    /// metric at all, which is what <see cref="AlertReasons.Monitoring"/> is for.
    /// </summary>
    public static string Reason(string? rule, AlertType type) => rule switch
    {
        StatisticalAlertRules.ActivityDeclineRule
            or StatisticalAlertRules.NoMorningActivityRule
            or StatisticalAlertRules.LongTermTrendRule => AlertReasons.Activity,
        StatisticalAlertRules.ElevatedHeartRateRule
            or RealtimeHeartRateRule
            or StatisticalAlertRules.HeartRateVariabilityDropRule
            or StatisticalAlertRules.ElevatedZoneWithoutMovementRule => AlertReasons.Heart,
        // Overnight breathing takes the monitoring icon rather than the heart one: the detail
        // screen's icons are hand-authored and there is no lungs artwork, and filing a breathing
        // finding under a heart icon would tell a caregiver the wrong organ at a glance.
        StatisticalAlertRules.OvernightBreathingUpRule => AlertReasons.Monitoring,
        StatisticalAlertRules.DaytimeInactivityBlockRule => AlertReasons.Activity,
        StatisticalAlertRules.IrregularSleepRule => AlertReasons.Sleep,
        StatisticalAlertRules.SleepOutsideRangeRule => AlertReasons.Sleep,
        StatisticalAlertRules.RestingHeartRateOutsideRangeRule => AlertReasons.Heart,
        // Monitoring, like overnight breathing: there is no oxygen artwork, and a heart icon
        // would name the wrong organ.
        StatisticalAlertRules.OxygenBelowRangeRule => AlertReasons.Monitoring,

        // The heart icon, not monitoring: a rhythm finding is the most literally cardiac thing
        // this screen shows. Without these two arms AlertType.Rhythm falls through to the default
        // below and the most consequential alert in the product gets the generic icon.
        StatisticalAlertRules.EcgAtrialFibrillationRule => AlertReasons.Heart,
        StatisticalAlertRules.IrregularRhythmRule => AlertReasons.Heart,
        DeviceSilenceRule => AlertReasons.Device,
        _ => type switch
        {
            // A markerless Inactivity row is the old device-silence producer (see DailyLogDays) —
            // it is about the watch having gone quiet, not about the wearer having slowed down.
            AlertType.Inactivity when rule is null => AlertReasons.Device,
            AlertType.Inactivity or AlertType.Trend => AlertReasons.Activity,
            AlertType.HeartRate => AlertReasons.Heart,
            AlertType.Sleep => AlertReasons.Sleep,
            _ => AlertReasons.Monitoring,
        },
    };

    private static AlertComparisonResponse? Comparison(
        string? rule,
        JsonElement metrics,
        PatternBaseline? baseline,
        DateOnly today,
        DateOnly aboutDate,
        DateTime triggeredAt)
    {
        if (rule is not null && rule.StartsWith(AlertRuleCatalogue.CustomRulePrefix, StringComparison.Ordinal))
            return CustomAlarmComparison(metrics);

        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule => StepsComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.LongTermTrendRule => TrendComparison(metrics),
            StatisticalAlertRules.ElevatedHeartRateRule => HeartRateComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.IrregularSleepRule => SleepComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.NoMorningActivityRule => NoMorningComparison(metrics, baseline),
            RealtimeHeartRateRule => RealtimeHeartComparison(metrics, baseline),
            StatisticalAlertRules.HeartRateVariabilityDropRule
                => HeartRateVariabilityComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.OvernightBreathingUpRule
                => OvernightBreathingComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.ElevatedZoneWithoutMovementRule
                => ElevatedZoneComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.DaytimeInactivityBlockRule
                => SedentaryStretchComparison(metrics, baseline),
            DeviceSilenceRule => DeviceSilenceComparison(metrics, triggeredAt),
            _ => null,
        };
    }

    /// <summary>
    /// How long the watch has been quiet, against the stretch that raises the alert. There is no
    /// baseline here and there should not be one: the card answers "how far past the line is this"
    /// for a rule that is about the device, and putting a health figure in a silence card would
    /// suggest a reading exists for the hours whose whole point is that none does.
    /// </summary>
    private static AlertComparisonResponse? DeviceSilenceComparison(JsonElement metrics, DateTime triggeredAt)
    {
        var lastData = ReadDateTime(metrics, "lastDataUtc");
        var threshold = ReadDecimal(metrics, "thresholdMinutes");
        if (lastData is null && threshold is null)
            return null;

        // Measured to the firing instant rather than to now: a caregiver opening this alert two
        // days later is reading about the silence that raised it, not about a gap that has been
        // growing on the detail screen ever since.
        decimal? quiet = lastData is { } last && AsUtc(triggeredAt) > last
            ? (decimal)(AsUtc(triggeredAt) - last).TotalMinutes
            : null;

        static string Span(decimal? minutes) =>
            minutes is { } m ? $"{m / 60m:0.#} h" : "—";

        return new AlertComparisonResponse
        {
            CurrentLabel = "Quiet for",
            CurrentValue = Span(quiet),
            NormalLabel = "Alerts after",
            NormalValue = Span(threshold),
            ChangeLabel = ChangeLabel(quiet, threshold, "the quiet limit"),
            ChangePercent = ChangePercent(quiet, threshold),
        };
    }

    /// <summary>
    /// A caregiver-defined alarm against the level that caregiver set. <c>effectiveThreshold</c>
    /// in preference to the configured one: a baseline-relative alarm is configured as a percentage
    /// and fires on whatever figure that resolved to on the day, and the resolved figure is the one
    /// the reading beside it can be checked against.
    /// </summary>
    private static AlertComparisonResponse? CustomAlarmComparison(JsonElement metrics)
    {
        var observed = ReadDecimal(metrics, "observedValue");
        var threshold = ReadDecimal(metrics, "effectiveThreshold") ?? ReadDecimal(metrics, "configuredThreshold");
        if (observed is null && threshold is null)
            return null;

        var unit = AlarmUnit(metrics);

        string Level(decimal? value) => value is { } v
            ? (unit is null ? $"{v:0.#}" : $"{v:0.#} {unit}")
            : "—";

        return new AlertComparisonResponse
        {
            CurrentLabel = "Measured",
            CurrentValue = Level(observed),
            NormalLabel = "Your level",
            NormalValue = Level(threshold),
            ChangeLabel = ChangeLabel(observed, threshold, "the level you set"),
            ChangePercent = ChangePercent(observed, threshold),
        };
    }

    /// <summary>
    /// The unit an alarm's metric is quoted in, from the same catalogue the builder offered it
    /// from. Null when the stamp names a metric this build does not know — a figure with no unit
    /// beats a figure with the wrong one.
    /// </summary>
    internal static string? AlarmUnit(JsonElement metrics) =>
        Enum.TryParse<AlarmMetric>(ReadString(metrics, "metric"), out var metric)
            ? AlarmMetricCatalogue.Find(metric)?.Unit
            : null;

    /// <summary>
    /// An instant as UTC, whatever kind it arrived as. Rows read back from Postgres come through
    /// unspecified, and a fixture may hand this a local one.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static AlertComparisonResponse? HeartRateVariabilityComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var current = ReadDecimal(metrics, "heartRateVariabilityMs");
        var usual = ReadDecimal(metrics, "baselineAvgHeartRateVariabilityMs")
            ?? baseline?.AvgHeartRateVariabilityMs;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = DayLabel(aboutDate, today) ?? "Last night",
            CurrentValue = current is { } hrv ? $"{hrv:0.#} ms" : "—",
            NormalLabel = "Usual night",
            NormalValue = usual is { } avg ? $"{avg:0.#} ms" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    private static AlertComparisonResponse? OvernightBreathingComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var current = ReadDecimal(metrics, "overnightBreathingRate");
        var usual = ReadDecimal(metrics, "baselineAvgOvernightBreathingRate")
            ?? baseline?.AvgOvernightBreathingRate;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = DayLabel(aboutDate, today) ?? "Last night",
            CurrentValue = current is { } breathing ? $"{breathing:0.#}/min" : "—",
            NormalLabel = "Usual night",
            NormalValue = usual is { } avg ? $"{avg:0.#}/min" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    /// <summary>
    /// Zone minutes against the member's own usual — the half of this alert that is unusual. The
    /// step count is the other half and is already in the copy; putting both in a two-column
    /// comparison would leave a reader deciding which number the alert is about.
    /// </summary>
    private static AlertComparisonResponse? ElevatedZoneComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var current = ReadDecimal(metrics, "elevatedZoneMinutes");
        var usual = ReadDecimal(metrics, "baselineAvgElevatedZoneMinutes")
            ?? baseline?.AvgElevatedZoneMinutes;
        if (current is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = DayLabel(aboutDate, today) ?? "Yesterday",
            CurrentValue = $"{current:0} min raised",
            NormalLabel = "Usual day",
            NormalValue = usual is { } avg ? $"{avg:0} min" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    /// <summary>
    /// The longest still stretch against the member's usual longest, with the gap told in hours
    /// rather than the percentage the other cards quote. Both figures above the band are durations,
    /// and "62% above usual" left a caregiver to turn a percentage of a stretch back into time to
    /// know what it meant; "2.4 h longer" is the subtraction they would have done themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The left column names the measure rather than the day. "6.2 h" under a date said nothing
    /// about which of the day's figures it was, and the day is already on the chart headline and
    /// the alert list row this screen was opened from.
    /// </para>
    /// <para>
    /// The gap is taken between the two figures as shown, not the minutes behind them: 374 against
    /// 226 minutes reads 6.2 h and 3.8 h, and a band saying 2.5 h under those would look like a
    /// sum the card got wrong. Figures that show the same are "In line with usual" for the same
    /// reason, and <see cref="AlertComparisonResponse.ChangePercent"/> is zeroed with them so the
    /// arrow cannot point at a difference the words just said is not there.
    /// </para>
    /// </remarks>
    private static AlertComparisonResponse? SedentaryStretchComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var current = ReadDecimal(metrics, "longestSedentaryStretchMinutes");
        var usual = ReadDecimal(metrics, "baselineAvgLongestSedentaryStretchMinutes")
            ?? baseline?.AvgLongestSedentaryStretchMinutes;
        if (current is null)
            return null;

        // Away from zero because that is how "0.#" rounds a midpoint. Math.Round's default
        // banker's rounding would show 219 minutes as 3.7 h and subtract it as 3.6.
        static decimal Shown(decimal minutes) => Math.Round(minutes / 60m, 1, MidpointRounding.AwayFromZero);
        static string Span(decimal hours) => string.Create(CultureInfo.InvariantCulture, $"{hours:0.#} h");

        var shownCurrent = Shown(current.Value);
        decimal? shownUsual = usual is { } avg ? Shown(avg) : null;
        // No usual yet, no band — the same gate the shared ChangeLabel applies.
        var gap = usual is > 0 ? shownCurrent - shownUsual : null;

        return new AlertComparisonResponse
        {
            CurrentLabel = "Longest still stretch",
            CurrentValue = Span(shownCurrent),
            NormalLabel = "Usual longest",
            NormalValue = shownUsual is { } shown ? Span(shown) : "—",
            ChangeLabel = gap switch
            {
                null => null,
                0 => "In line with usual",
                < 0 => $"{Span(-gap.Value)} shorter than usual",
                _ => $"{Span(gap.Value)} longer than usual",
            },
            ChangePercent = gap == 0 ? 0 : ChangePercent(current, usual),
        };
    }

    private static AlertComparisonResponse? StepsComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var current = ReadDecimal(metrics, "steps");
        var usual = ReadDecimal(metrics, "baselineAvgSteps") ?? baseline?.AvgSteps;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = DayLabel(aboutDate, today) ?? "Yesterday",
            CurrentValue = current is { } steps ? $"{steps:N0} steps" : "—",
            NormalLabel = "Usual day",
            NormalValue = usual is { } avg ? $"{avg:N0} steps" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    private static AlertComparisonResponse? TrendComparison(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object
            || !metrics.TryGetProperty("weeklyAvgSteps", out var weeks)
            || weeks.ValueKind != JsonValueKind.Array
            || weeks.GetArrayLength() < 2)
            return null;

        var oldest = ReadDecimal(weeks[0]);
        var newest = ReadDecimal(weeks[weeks.GetArrayLength() - 1]);
        var fraction = ReadDecimal(metrics, "declineFraction");

        return new AlertComparisonResponse
        {
            CurrentLabel = "This month",
            CurrentValue = newest is { } n ? $"{n:N0} steps/day" : "—",
            NormalLabel = "A month ago",
            NormalValue = oldest is { } o ? $"{o:N0} steps/day" : "—",
            ChangeLabel = fraction is { } f
                ? $"{f * 100:0}% below a month ago"
                : ChangeLabel(newest, oldest, "a month ago"),
            // The rule reports its decline as a positive fraction of the earlier figure; the
            // signed form of "below" is negative.
            ChangePercent = fraction is { } decline
                ? -Math.Round(decline * 100m, 0)
                : ChangePercent(newest, oldest),
        };
    }

    private static AlertComparisonResponse? HeartRateComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var current = ReadDecimal(metrics, "restingHeartRate");
        var usual = ReadDecimal(metrics, "baselineAvgRestingHeartRate") ?? baseline?.AvgRestingHeartRate;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = DayLabel(aboutDate, today) ?? "Yesterday",
            CurrentValue = current is { } bpm ? $"{bpm:N0} bpm" : "—",
            NormalLabel = "Usual",
            NormalValue = usual is { } avg ? $"{avg:N0} bpm" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    private static AlertComparisonResponse? SleepComparison(
        JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        var currentMinutes = ReadDecimal(metrics, "sleepMinutes");
        var usualMinutes = ReadDecimal(metrics, "baselineAvgSleepMinutes") ?? baseline?.AvgSleepMinutes;
        if (currentMinutes is null && usualMinutes is null)
            return null;

        return new AlertComparisonResponse
        {
            // The night that ended this morning is "Last night". An older card must name
            // the night it judged — the chart headline already does.
            CurrentLabel = aboutDate == today
                ? "Last night"
                : DayLabel(aboutDate, today) ?? "Last night",
            CurrentValue = HoursLabel(currentMinutes),
            NormalLabel = "Usual night",
            NormalValue = HoursLabel(usualMinutes),
            ChangeLabel = ChangeLabel(currentMinutes, usualMinutes, "usual"),
            ChangePercent = ChangePercent(currentMinutes, usualMinutes),
        };
    }

    private static AlertComparisonResponse NoMorningComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var wake = ReadString(metrics, "typicalWakeTime")
            ?? baseline?.TypicalWakeTime?.ToString("HH:mm", CultureInfo.InvariantCulture);

        return new AlertComparisonResponse
        {
            CurrentLabel = "Today",
            CurrentValue = "0 steps",
            NormalLabel = "Usual wake",
            NormalValue = wake ?? "—",
            ChangeLabel = "No movement since waking time",
        };
    }

    private static AlertComparisonResponse? RealtimeHeartComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var current = ReadDecimal(metrics, "hrTrendLast");
        var usual = baseline?.AvgRestingHeartRate;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = "This hour",
            CurrentValue = current is { } bpm ? $"{bpm:N0} bpm" : "—",
            NormalLabel = "Usual resting",
            NormalValue = usual is { } avg ? $"{avg:N0} bpm" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
            ChangePercent = ChangePercent(current, usual),
        };
    }

    private static AlertChartResponse? Chart(
        string? rule,
        IReadOnlyList<ActivityLog> logs,
        DateOnly today,
        GranularWindow? granular,
        PatternBaseline? baseline,
        JsonElement metrics,
        CardiMember? member,
        ElapsedSteps? elapsedSteps,
        DateOnly aboutDate)
    {
        // Only the step windows run up to the day in progress — see NeedsElapsedMatch.
        var partialDay = NeedsElapsedMatch(rule) ? today : (DateOnly?)null;

        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule
                => DailyChart(
                    "steps", "Activity", "steps", ActivityDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps ?? ReadDecimal(metrics, "baselineAvgSteps"),
                    partialDay, elapsedSteps, headlineDate: aboutDate),

            // About-day is the morning in progress. Headlining that 0 would fight
            // NeedsElapsedMatch; the flag still marks today via AboutDate.
            StatisticalAlertRules.NoMorningActivityRule
                => DailyChart(
                    "steps", "Activity", "steps", ActivityDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps ?? ReadDecimal(metrics, "baselineAvgSteps"),
                    partialDay, elapsedSteps),

            StatisticalAlertRules.LongTermTrendRule
                => DailyChart(
                    "steps", "Activity", "steps", TrendDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps, partialDay, elapsedSteps, headlineDate: aboutDate),

            StatisticalAlertRules.ElevatedHeartRateRule
                => DailyChart(
                    "restingHeartRate", "Heart Rate", "bpm", HeartRateDays, today, logs,
                    l => l.RestingHeartRate,
                    baseline?.AvgRestingHeartRate ?? ReadDecimal(metrics, "baselineAvgRestingHeartRate"),
                    headlineDate: aboutDate),

            StatisticalAlertRules.IrregularSleepRule
                => DailyChart(
                    "sleep", "Sleep", "hours", SleepDays, today, logs,
                    l => Hours(l.SleepMinutes),
                    Hours(baseline?.AvgSleepMinutes) ?? Hours(ReadDecimal(metrics, "baselineAvgSleepMinutes")),
                    reference: SleepReference(metrics, member, today),
                    headlineDate: aboutDate),

            StatisticalAlertRules.OvernightBreathingUpRule
                => DailyChart(
                    "overnightBreathingRate", "Overnight Breathing", "brpm", HeartRateDays, today, logs,
                    l => l.OvernightBreathingRate,
                    baseline?.AvgOvernightBreathingRate
                        ?? ReadDecimal(metrics, "baselineAvgOvernightBreathingRate"),
                    // No band: WHO's 12–20 is a waking rate, not a sleeping one
                    // (HealthReferenceRanges.NoOvernightBreathingBand).
                    reference: null,
                    headlineDate: aboutDate),

            StatisticalAlertRules.ElevatedZoneWithoutMovementRule
                => DailyChart(
                    "elevatedZoneMinutes", "Raised Heart-Rate Minutes", "minutes", ActivityDays, today, logs,
                    l => BaselineCalculator.ElevatedZoneMinutes(l),
                    baseline?.AvgElevatedZoneMinutes,
                    headlineDate: aboutDate),

            StatisticalAlertRules.DaytimeInactivityBlockRule
                => DailyChart(
                    "longestSedentaryStretch", "Longest Still Stretch", "hours", ActivityDays, today, logs,
                    // Halves away from zero, as the comparison card below the chart rounds them
                    // (SedentaryStretchComparison): 219 minutes is 3.65 h, and banker's rounding
                    // here would headline 3.6 above a card that says 3.7.
                    l => l.LongestSedentaryStretchMinutes is { } m
                        ? Math.Round(m / 60m, 1, MidpointRounding.AwayFromZero)
                        : null,
                    baseline?.AvgLongestSedentaryStretchMinutes is { } avg
                        ? Math.Round(avg / 60m, 1, MidpointRounding.AwayFromZero)
                        : null,
                    headlineDate: aboutDate),

            StatisticalAlertRules.HeartRateVariabilityDropRule
                => DailyChart(
                    "heartRateVariability", "Heart Rate Variability", "ms", HeartRateDays, today, logs,
                    l => l.HeartRateVariabilityMs,
                    baseline?.AvgHeartRateVariabilityMs
                        ?? ReadDecimal(metrics, "baselineAvgHeartRateVariabilityMs"),
                    headlineDate: aboutDate),

            RealtimeHeartRateRule => GranularHeartChart(granular, baseline?.AvgRestingHeartRate),

            // The published-range rules draw the range they were judged against as the chart's
            // reference — the thing the finding is about — with the member's usual as the line.
            StatisticalAlertRules.SleepOutsideRangeRule
                => DailyChart(
                    "sleep", "Sleep", "hours", SleepDays, today, logs,
                    l => Hours(l.SleepMinutes),
                    Hours(baseline?.AvgSleepMinutes),
                    reference: SleepReference(metrics, member, today),
                    headlineDate: aboutDate),

            StatisticalAlertRules.RestingHeartRateOutsideRangeRule
                => DailyChart(
                    "restingHeartRate", "Heart Rate", "bpm", SleepDays, today, logs,
                    l => l.RestingHeartRate,
                    baseline?.AvgRestingHeartRate,
                    reference: HealthReferenceRanges.RestingHeartRate,
                    headlineDate: aboutDate),

            StatisticalAlertRules.OxygenBelowRangeRule
                => DailyChart(
                    "spo2", "Blood Oxygen", "%", SleepDays, today, logs,
                    l => l.SpO2Average,
                    baseline: null,
                    reference: HealthReferenceRanges.SpO2,
                    headlineDate: aboutDate),

            _ => null,
        };
    }

    /// <summary>
    /// The published band the sleep chart shades behind the line. The figures the rule stored win
    /// over anything re-derived from the member's date of birth, so an alert raised before they
    /// crossed <see cref="HealthReferenceRanges.OlderAdultAge"/> keeps drawing the band its own
    /// copy quotes — the same reason the rule writes them down in the first place. Rows from
    /// before the band was stored fall back to the member's age today, and an alert composed
    /// without a member gets no band rather than a guessed one.
    /// </summary>
    private static MetricReference? SleepReference(JsonElement metrics, CardiMember? member, DateOnly today)
    {
        // irregular_sleep stores the band as recommended*Hours; sleep_outside_range as range*,
        // the shape every published-range rule shares. Either way it is the band the night was
        // judged against, which must win over the member's current age — a member who has since
        // turned 65 would otherwise have a 7-9 judgement drawn against 7-8.
        if ((ReadDecimal(metrics, "recommendedLowHours") ?? ReadDecimal(metrics, "rangeLow")) is { } low
            && (ReadDecimal(metrics, "recommendedHighHours") ?? ReadDecimal(metrics, "rangeHigh")) is { } high)
        {
            return new MetricReference
            {
                Low = low,
                High = high,
                Source = HealthReferenceRanges.SleepSource,
                // The stored figures are the same NSF band HealthReferenceRanges.Sleep returns.
                IsPublishedNormal = true,
            };
        }

        return member is null
            ? null
            : HealthReferenceRanges.Sleep(member.DateOfBirth.ToAgeInYears(today));
    }

    /// <param name="partialDay">
    /// The day still in progress, or null for a metric whose daily figure is settled when reported.
    /// </param>
    /// <param name="reference">
    /// The published typical-adult band, or null for a metric that has none — see
    /// <see cref="AlertChartResponse.Reference"/>.
    /// </param>
    /// <param name="headlineDate">
    /// The civil day this alert is about. When that day is on the series and is a finished
    /// reading, the headline quotes it rather than the latest settled slot — otherwise a later
    /// quiet Tuesday would caption a card about last Thursday, and the yellow about-day mark
    /// would sit on a different point from the number above the chart.
    /// </param>
    private static AlertChartResponse? DailyChart(
        string metric,
        string name,
        string unit,
        int days,
        DateOnly today,
        IReadOnlyList<ActivityLog> logs,
        Func<ActivityLog, decimal?> selector,
        decimal? baseline,
        DateOnly? partialDay = null,
        ElapsedSteps? elapsedSteps = null,
        MetricReference? reference = null,
        DateOnly? headlineDate = null)
    {
        var byDate = logs
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());

        var series = new List<MetricPoint>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            series.Add(new MetricPoint
            {
                Date = date,
                Value = byDate.TryGetValue(date, out var log) ? selector(log) : null,
                IsPartial = date == partialDay,
            });
        }

        if (series.All(p => p.Value is null))
            return null;

        // The headline quotes a finished day. Taking the latest reading outright is what put a
        // lunchtime step count in the header of a card whose comparison block was reporting the
        // whole of yesterday — two different days, one number apiece, and nothing on screen saying
        // so. Preferring the about-day keeps the number, the comparison and the flagged point on
        // the same reading.
        var about = headlineDate is { } day
            ? series.FirstOrDefault(p => p.Date == day && p.Value is not null && !p.IsPartial)
            : null;
        var settled = about ?? series.LastOrDefault(p => p.Value is not null && !p.IsPartial);

        return new AlertChartResponse
        {
            Metric = metric,
            Name = name,
            Unit = unit,
            WindowLabel = $"Last {days} days",
            Value = settled?.Value,
            ValueLabel = about is not null || partialDay is not null
                ? DayLabel(settled?.Date, today)
                : null,
            Baseline = baseline,
            Reference = reference,
            Series = series,
            PartialDayLabel = PartialDayLabel(elapsedSteps),
        };
    }

    /// <summary>
    /// The ask that follows "the still stretch began around …". An evening start is settling
    /// down, not an afternoon in a chair — the same distinction the sleep-window clip exists
    /// to make, in words.
    /// </summary>
    public static string StillStretchAsk(TimeOnly localStart, TimeOnly? typicalBedtime)
    {
        var evening = typicalBedtime ?? new TimeOnly(20, 0);
        return IsAtOrAfterBedtime(localStart, evening)
            ? "whether they settled early"
            : "whether anything kept them in the chair";
    }

    /// <summary>
    /// Whether <paramref name="localStart"/> sits in the hours after bedtime, the short way
    /// round the clock. A 01:00 bedtime is after midnight, so 21:00 is still evening in a
    /// chair — the linear <c>&gt;=</c> that treated every afternoon as already after 01:00
    /// is what this replaces. The window is eight hours: bedtime through the small hours,
    /// not the next afternoon.
    /// </summary>
    private static bool IsAtOrAfterBedtime(TimeOnly localStart, TimeOnly bedtime)
    {
        var raw = (int)Math.Round((localStart.ToTimeSpan() - bedtime.ToTimeSpan()).TotalMinutes);
        var forward = ((raw % 1440) + 1440) % 1440;
        return forward < 8 * 60;
    }

    /// <summary>Names the day the headline belongs to, relative to the caregiver's today.</summary>
    private static string? DayLabel(DateOnly? day, DateOnly today) => day switch
    {
        null => null,
        { } d when d == today => "Today",
        { } d when d == today.AddDays(-1) => "Yesterday",
        { } d => d.ToString("d MMM", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The one sentence that makes the day in progress readable: both figures cover the same
    /// elapsed minutes, so the percentage between them is a claim worth making. Null unless both
    /// halves are present — a running total with nothing to sit against is just the number the
    /// chart already plots.
    /// </summary>
    private static string? PartialDayLabel(ElapsedSteps? elapsed)
    {
        if (elapsed?.Today is not { } today)
            return null;

        var soFar = $"{today:N0} steps so far today";
        if (elapsed.Comparison is not { } comparison || comparison <= 0)
            return soFar;

        var percent = Math.Round((today - comparison) / comparison * 100m, 0);
        var change = percent switch
        {
            0 => "in line with",
            < 0 => $"{Math.Abs(percent):0}% below",
            _ => $"{percent:0}% above",
        };

        return $"{soFar}, {change} the {comparison:N0} by this time yesterday";
    }

    private static AlertChartResponse? GranularHeartChart(GranularWindow? granular, decimal? baseline)
    {
        if (granular is null
            || !granular.MinuteSeries.TryGetValue(GranularMetric.HeartRate, out var samples)
            || samples.Length == 0)
            return null;

        var step = Math.Max(1, (int)Math.Ceiling(samples.Length / (double)GranularMaxPoints));
        var series = new List<MetricPoint>();
        decimal? latest = null;

        void Add(int i)
        {
            decimal? value = samples[i] is { } sample ? (decimal)sample : null;
            if (value is not null)
                latest = value;
            series.Add(new MetricPoint
            {
                Date = DateOnly.FromDateTime(granular.FromUtc.AddMinutes(i)),
                Value = value,
            });
        }

        for (var i = 0; i < samples.Length; i += step)
            Add(i);

        // A stride that divides the window can skip the closing minute; the headline value
        // should still be that last reading. Stay at GranularMaxPoints by replacing the
        // last strided point rather than growing the series.
        var last = samples.Length - 1;
        if (last > 0 && last % step != 0)
        {
            if (series.Count >= GranularMaxPoints)
                series.RemoveAt(series.Count - 1);
            Add(last);
        }

        if (series.Count < 2 || series.All(p => p.Value is null))
            return null;

        return new AlertChartResponse
        {
            Metric = "heartRate",
            Name = "Heart Rate",
            Unit = "bpm",
            WindowLabel = "This hour",
            Value = latest,
            Baseline = baseline,
            Series = series,
        };
    }

    private static DateOnly? LastMeasuredStepsDay(IReadOnlyList<ActivityLog> logs) =>
        logs
            .Where(l => l.Steps is > 0)
            .Select(l => l.Date)
            .DefaultIfEmpty()
            .Max() is { } day && day != default
            ? day
            : null;

    /// <summary>
    /// Third reader of the same two columns, so it goes through the one mapper the alerts list
    /// and the dashboard strip already share — see <see cref="AlertLifecycle"/>. A detail page
    /// that disagreed with the row a caregiver tapped to reach it is the exact failure this
    /// branch started from.
    /// </summary>
    private static string StatusLabel(Alert alert) => AlertLifecycle.StatusLabel(alert);

    /// <summary>
    /// The deviation behind <see cref="ChangeLabel"/> as a signed whole percent — see
    /// <see cref="AlertComparisonResponse.ChangePercent"/>. Same inputs and same rounding, so the
    /// number and the sentence can never describe different movements.
    /// </summary>
    private static decimal? ChangePercent(decimal? current, decimal? usual) =>
        current is not { } c || usual is not > 0
            ? null
            : Math.Round((c - usual.Value) / usual.Value * 100m, 0);

    private static string? ChangeLabel(decimal? current, decimal? usual, string usualWord)
    {
        if (current is not { } c || usual is not > 0)
            return null;

        var percent = Math.Round((c - usual.Value) / usual.Value * 100m, 0);
        if (percent == 0)
            return $"In line with {usualWord}";
        return percent < 0
            ? $"{Math.Abs(percent):0}% below {usualWord}"
            : $"{percent:0}% above {usualWord}";
    }

    private static string HoursLabel(decimal? minutes) =>
        minutes is { } m ? $"{Hours(m):0.#} hours" : "—";

    /// <summary>
    /// Minutes as hours, at the tenth the sleep card is read to. Rounded here rather than left to
    /// whatever formats it: <c>MemberInsightsCalculator</c> rounds the dashboard's series the same
    /// way, and an unrounded quotient put 200 minutes on the wire as
    /// <c>3.3333333333333333333333333333</c> — 28 significant figures of a wearable's nearest
    /// minute, in a payload the detail screen re-polls the whole time it is open.
    /// </summary>
    private static decimal? Hours(decimal? minutes) =>
        minutes is { } m ? Math.Round(m / 60m, 1) : null;

    private static bool TryParse(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static decimal? ReadDecimal(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p)
            ? ReadDecimal(p)
            : null;

    private static decimal? ReadDecimal(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number when p.TryGetDecimal(out var d) => d,
        JsonValueKind.Number => (decimal)p.GetDouble(),
        JsonValueKind.String when decimal.TryParse(
            p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };

    internal static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static DateTime? ReadDateTime(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Null)
            return null;
        if (p.ValueKind == JsonValueKind.String
            && DateTime.TryParse(
                p.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime();
        }

        return null;
    }

    private static DateOnly? ReadDateOnly(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Null)
            return null;
        if (p.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        return null;
    }

    /// <summary>
    /// Local midnight as an instant. A clock change can delete midnight outright (it happens in
    /// Chile, Cuba, Iran and elsewhere), and <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime,
    /// TimeZoneInfo)"/> throws on a local time that never occurred — so the day starts at the
    /// first minute that did exist rather than the request failing one night a year.
    /// </summary>
    private static DateTime LocalMidnightUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static DateTime FloorHour(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime CeilHour(DateTime utc)
    {
        var floor = FloorHour(utc);
        return utc == floor ? floor : floor.AddHours(1);
    }
}
