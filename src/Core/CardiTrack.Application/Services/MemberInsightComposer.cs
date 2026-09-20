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
    public static MemberInsightResponse? Compose(
        MemberInsight? baseline, MemberInsight? trend, DateTime utcNow)
    {
        var servableBaseline = InsightServability.IsServable(baseline, utcNow) ? baseline : null;
        var servableTrend = InsightServability.IsServable(trend, utcNow) ? trend : null;

        if (servableBaseline is null && servableTrend is null)
            return null;

        return new MemberInsightResponse
        {
            Summary = servableBaseline?.Summary,
            KeyFindings = Findings(servableBaseline),
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

    private static DateTime? Newest(DateTime? first, DateTime? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => first > second ? first : second,
        };
}
