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
        DeviceSilenceRule => 0,
        RealtimeHeartRateRule => 0,
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
    public static bool IsAboutPreviousLocalDay(string? rule) => rule is
        StatisticalAlertRules.ActivityDeclineRule
        or StatisticalAlertRules.ElevatedHeartRateRule
        or StatisticalAlertRules.LongTermTrendRule;

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
        DateOnly? firedOn = null)
    {
        var rule = ReadRule(alert.MetricValues);
        TryParse(alert.MetricValues, out var metrics);
        var raisedOn = firedOn ?? DateOnly.FromDateTime(
            alert.TriggeredDate.Kind == DateTimeKind.Local
                ? alert.TriggeredDate.ToUniversalTime()
                : DateTime.SpecifyKind(alert.TriggeredDate, DateTimeKind.Utc));
        var aboutDate = AboutDate(rule, alert.MetricValues, raisedOn);

        return new AlertDetailResponse
        {
            Reason = Reason(rule, alert.AlertType),
            AlertId = alert.Id,
            CardiMemberId = alert.CardiMemberId,
            CardiMemberName = member?.Name ?? string.Empty,
            CardiMemberPhotoUrl = null,
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
            Comparison = Comparison(rule, metrics, baseline, today, aboutDate),
            Chart = Chart(rule, logs, today, granular, baseline, metrics, elapsedSteps),
            LastActivityOn = LastMeasuredStepsDay(logs),
            TypicalWakeTime = ReadString(metrics, "typicalWakeTime")
                ?? baseline?.TypicalWakeTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            LastDataAt = ReadDateTime(metrics, "lastDataUtc"),
        };
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
        StatisticalAlertRules.ElevatedHeartRateRule or RealtimeHeartRateRule => AlertReasons.Heart,
        StatisticalAlertRules.IrregularSleepRule => AlertReasons.Sleep,
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
        string? rule, JsonElement metrics, PatternBaseline? baseline, DateOnly today, DateOnly aboutDate)
    {
        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule => StepsComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.LongTermTrendRule => TrendComparison(metrics),
            StatisticalAlertRules.ElevatedHeartRateRule => HeartRateComparison(metrics, baseline, today, aboutDate),
            StatisticalAlertRules.IrregularSleepRule => SleepComparison(metrics, baseline),
            StatisticalAlertRules.NoMorningActivityRule => NoMorningComparison(metrics, baseline),
            RealtimeHeartRateRule => RealtimeHeartComparison(metrics, baseline),
            _ => null,
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

    private static AlertComparisonResponse? SleepComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var currentMinutes = ReadDecimal(metrics, "sleepMinutes");
        var usualMinutes = ReadDecimal(metrics, "baselineAvgSleepMinutes") ?? baseline?.AvgSleepMinutes;
        if (currentMinutes is null && usualMinutes is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = "Last night",
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
        ElapsedSteps? elapsedSteps)
    {
        // Only the step windows run up to the day in progress — see NeedsElapsedMatch.
        var partialDay = NeedsElapsedMatch(rule) ? today : (DateOnly?)null;

        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule
                or StatisticalAlertRules.NoMorningActivityRule
                => DailyChart(
                    "steps", "Activity", "steps", ActivityDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps ?? ReadDecimal(metrics, "baselineAvgSteps"),
                    partialDay, elapsedSteps),

            StatisticalAlertRules.LongTermTrendRule
                => DailyChart(
                    "steps", "Activity", "steps", TrendDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps, partialDay, elapsedSteps),

            StatisticalAlertRules.ElevatedHeartRateRule
                => DailyChart(
                    "restingHeartRate", "Heart Rate", "bpm", HeartRateDays, today, logs,
                    l => l.RestingHeartRate,
                    baseline?.AvgRestingHeartRate ?? ReadDecimal(metrics, "baselineAvgRestingHeartRate")),

            StatisticalAlertRules.IrregularSleepRule
                => DailyChart(
                    "sleep", "Sleep", "hours", SleepDays, today, logs,
                    l => l.SleepMinutes is { } minutes ? minutes / 60m : null,
                    Hours(baseline?.AvgSleepMinutes) ?? Hours(ReadDecimal(metrics, "baselineAvgSleepMinutes"))),

            RealtimeHeartRateRule => GranularHeartChart(granular, baseline?.AvgRestingHeartRate),

            _ => null,
        };
    }

    /// <param name="partialDay">
    /// The day still in progress, or null for a metric whose daily figure is settled when reported.
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
        ElapsedSteps? elapsedSteps = null)
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
        // so.
        var settled = series.LastOrDefault(p => p.Value is not null && !p.IsPartial);

        return new AlertChartResponse
        {
            Metric = metric,
            Name = name,
            Unit = unit,
            WindowLabel = $"Last {days} days",
            Value = settled?.Value,
            ValueLabel = partialDay is null ? null : DayLabel(settled?.Date, today),
            Baseline = baseline,
            Series = series,
            PartialDayLabel = PartialDayLabel(elapsedSteps),
        };
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
        minutes is { } m ? $"{m / 60m:0.#} hours" : "—";

    private static decimal? Hours(decimal? minutes) => minutes is { } m ? m / 60m : null;

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

    private static decimal? ReadDecimal(JsonElement obj, string name) =>
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

    private static string? ReadString(JsonElement obj, string name) =>
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
