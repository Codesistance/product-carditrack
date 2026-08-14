using System.Globalization;
using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services;

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

    public static AlertDetailResponse Compose(
        Alert alert,
        CardiMember? member,
        User? acknowledger,
        IReadOnlyList<ActivityLog> logs,
        DateOnly today,
        GranularWindow? granular,
        PatternBaseline? baseline)
    {
        var rule = ReadRule(alert.MetricValues);
        TryParse(alert.MetricValues, out var metrics);

        return new AlertDetailResponse
        {
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
            AcknowledgedAt = alert.AcknowledgedDate,
            AcknowledgedByUserId = alert.AcknowledgedByUserId,
            AcknowledgedByName = acknowledger?.Name,
            Comparison = Comparison(rule, metrics, baseline),
            Chart = Chart(rule, logs, today, granular, baseline, metrics),
            LastActivityOn = LastMeasuredStepsDay(logs),
            TypicalWakeTime = ReadString(metrics, "typicalWakeTime")
                ?? baseline?.TypicalWakeTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            LastDataAt = ReadDateTime(metrics, "lastDataUtc"),
        };
    }

    private static AlertComparisonResponse? Comparison(
        string? rule, JsonElement metrics, PatternBaseline? baseline)
    {
        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule => StepsComparison(metrics, baseline),
            StatisticalAlertRules.LongTermTrendRule => TrendComparison(metrics),
            StatisticalAlertRules.ElevatedHeartRateRule => HeartRateComparison(metrics, baseline),
            StatisticalAlertRules.IrregularSleepRule => SleepComparison(metrics, baseline),
            StatisticalAlertRules.NoMorningActivityRule => NoMorningComparison(metrics, baseline),
            RealtimeHeartRateRule => RealtimeHeartComparison(metrics, baseline),
            _ => null,
        };
    }

    private static AlertComparisonResponse? StepsComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var current = ReadDecimal(metrics, "steps");
        var usual = ReadDecimal(metrics, "baselineAvgSteps") ?? baseline?.AvgSteps;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = "Yesterday",
            CurrentValue = current is { } steps ? $"{steps:N0} steps" : "—",
            NormalLabel = "Usual day",
            NormalValue = usual is { } avg ? $"{avg:N0} steps" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
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
        };
    }

    private static AlertComparisonResponse? HeartRateComparison(JsonElement metrics, PatternBaseline? baseline)
    {
        var current = ReadDecimal(metrics, "restingHeartRate");
        var usual = ReadDecimal(metrics, "baselineAvgRestingHeartRate") ?? baseline?.AvgRestingHeartRate;
        if (current is null && usual is null)
            return null;

        return new AlertComparisonResponse
        {
            CurrentLabel = "Yesterday",
            CurrentValue = current is { } bpm ? $"{bpm:N0} bpm" : "—",
            NormalLabel = "Usual",
            NormalValue = usual is { } avg ? $"{avg:N0} bpm" : "—",
            ChangeLabel = ChangeLabel(current, usual, "usual"),
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
        };
    }

    private static AlertChartResponse? Chart(
        string? rule,
        IReadOnlyList<ActivityLog> logs,
        DateOnly today,
        GranularWindow? granular,
        PatternBaseline? baseline,
        JsonElement metrics)
    {
        return rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule
                or StatisticalAlertRules.NoMorningActivityRule
                => DailyChart(
                    "steps", "Activity", "steps", ActivityDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps ?? ReadDecimal(metrics, "baselineAvgSteps")),

            StatisticalAlertRules.LongTermTrendRule
                => DailyChart(
                    "steps", "Activity", "steps", TrendDays, today, logs,
                    l => l.Steps, baseline?.AvgSteps),

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

    private static AlertChartResponse? DailyChart(
        string metric,
        string name,
        string unit,
        int days,
        DateOnly today,
        IReadOnlyList<ActivityLog> logs,
        Func<ActivityLog, decimal?> selector,
        decimal? baseline)
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
            });
        }

        if (series.All(p => p.Value is null))
            return null;

        var latest = series.LastOrDefault(p => p.Value is not null)?.Value;

        return new AlertChartResponse
        {
            Metric = metric,
            Name = name,
            Unit = unit,
            WindowLabel = $"Last {days} days",
            Value = latest,
            Baseline = baseline,
            Series = series,
        };
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

    private static string StatusLabel(Alert alert) =>
        (alert.IsResolved ? AlertStatus.Resolved
            : alert.AcknowledgedDate is not null ? AlertStatus.Acknowledged
            : AlertStatus.New)
        .ToString().ToLowerInvariant();

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
