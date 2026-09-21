using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// The stored baseline and trend rows as the block a caregiver-facing payload carries.
/// </summary>
/// <remarks>
/// One composer rather than mapping at each call site, for the reason
/// <see cref="InsightServability"/> exists: the dashboard, the member detail screen and the
/// journal entry all show this, and three mappings would be three chances for one surface to
/// render a stale row another withheld.
/// </remarks>
public static class MemberInsightComposer
{
    /// <summary>
    /// The block, or null when neither row has anything servable to say — so a client can render
    /// the whole section on presence rather than testing each field.
    /// </summary>
    /// <param name="ageYears">
    /// The member's age, which reaches only the sleep band — the one published range here that
    /// splits on it.
    /// </param>
    public static MemberInsightResponse? Compose(
        MemberInsight? baseline, MemberInsight? trend, DateTime utcNow, int ageYears)
    {
        var servableBaseline = InsightServability.IsServable(baseline, utcNow) ? baseline : null;
        var servableTrend = InsightServability.IsServable(trend, utcNow) ? trend : null;

        if (servableBaseline is null && servableTrend is null)
            return null;

        return new MemberInsightResponse
        {
            Summary = servableBaseline?.Summary,
            KeyFindings = Findings(servableBaseline),
            Movements = Movements(servableBaseline, ageYears),
            Trend = servableTrend?.Summary,
            TrendFindings = Findings(servableTrend),
            // From the baseline row alone: the trend pass never runs for a member still being
            // learned, so a trend row can say nothing about this and reading it here would let an
            // absent trend look like an established baseline.
            IsLearning = servableBaseline?.IsLearning ?? true,
            IsProvisional = servableBaseline?.IsProvisional ?? false,
            BaselinePeriodDays = servableBaseline?.BaselinePeriodDays,
            GeneratedAt = Newest(servableBaseline?.GeneratedAtUtc, servableTrend?.GeneratedAtUtc),
        };
    }

    /// <summary>
    /// The stored findings back as a list. Newline-joined on the way in, so a blank line does not
    /// come back as an empty bullet.
    /// </summary>
    public static IReadOnlyList<string> Findings(MemberInsight? insight) =>
        string.IsNullOrWhiteSpace(insight?.KeyFindings)
            ? []
            : insight.KeyFindings.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The stored movements, graded. Graded here rather than at write time so that revising how a
    /// movement is worded or judged reaches every caregiver on their next request, instead of
    /// waiting for a pass to rewrite rows that already hold the measurements.
    /// </summary>
    private static IReadOnlyList<MemberMovementResponse> Movements(
        MemberInsight? insight, int ageYears)
    {
        var movements = InsightMovements.Read(insight?.Movements);
        if (movements.Count == 0)
            return [];

        var graded = new List<MemberMovementResponse>(movements.Count);
        foreach (var movement in movements)
        {
            var grading = MetricValence.Grade(movement, ageYears);
            graded.Add(new MemberMovementResponse
            {
                Metric = movement.Kind.ToString(),
                Label = movement.Metric,
                Headline = BaselineMovementCalculator.Headline(movement),
                Valence = Wire(grading.Valence),
                Basis = grading.Basis,
                Unit = movement.Unit,
                Recent = movement.Recent,
                Usual = movement.Usual,
                DeviationPercent = movement.DeviationPercent,
                AlarmMetric = grading.Alarm?.ToString(),
                SuggestedThresholdPercent = grading.Alarm is null
                    ? null
                    : MetricValence.SuggestedPercentThreshold(movement),
            });
        }

        return graded;
    }

    /// <summary>
    /// The wire vocabulary, spelled out rather than lowercased from the enum, so renaming a
    /// member cannot silently change what a shipped client is matching on.
    /// </summary>
    private static string Wire(MovementValence valence) => valence switch
    {
        MovementValence.Favourable => "favourable",
        MovementValence.WorthAttention => "attention",
        _ => "neutral",
    };

    private static DateTime? Newest(DateTime? first, DateTime? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => first > second ? first : second,
        };
}
