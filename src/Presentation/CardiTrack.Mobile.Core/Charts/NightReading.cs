using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// How a night reads on the app's sleep surfaces — the dashboard tile, the Member Detail trend, the
/// chat charts and the alert chart — knowing what the server knows about it
/// (<see cref="NightSleepStatus"/>, decision 2026-09-25).
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="NightSleepStatus.Awake"/> night is stored as 0 minutes so every average counts it,
/// and "0 hours" or "0m" is the one rendering of it that must never reach a caregiver: it reads as
/// a figure missing its digits, or as a watch that measured nothing — the opposite of what the
/// status establishes (the watch was worn all night and no sleep was recorded). A
/// <see cref="NightSleepStatus.Pending"/> night has not synced yet and may still arrive, which is
/// a different thing from a night nothing reached us for; the second stays a gap, as it always was.
/// </para>
/// <para>
/// The long forms are the server's own (<see cref="ReadingFigures"/>), so a chart callout and the
/// AI reply above it name one night one way. The short forms exist only because a half-width tile
/// has no room for the long one.
/// </para>
/// </remarks>
public static class NightReading
{
    /// <summary>The value slot of a tile too narrow for anything longer.</summary>
    public const string AwakeValue = "Awake";

    /// <summary>
    /// Under <see cref="AwakeValue"/> on the tile: the evidence, cut down from
    /// <see cref="ReadingFigures.AwakeNight"/>'s parenthesis.
    /// </summary>
    public const string AwakeCaption = "Watch worn, no sleep";

    /// <summary>A tapped awake night on a chart, and the legend entry for its mark.</summary>
    public const string AwakeCallout = "Awake all night";

    /// <summary>
    /// The tile's caption while last night has not synced and the figure above it is the night
    /// before's — the plain "Last night" it used to carry was then naming the wrong night.
    /// </summary>
    public const string NightBeforeCaption = "Night before · last night " + ReadingFigures.PendingNight;

    /// <summary>The same, when there is no earlier night to show either.</summary>
    public const string PendingCaption = "Last night " + ReadingFigures.PendingNight;

    /// <summary>Whether this point is a night the watch was worn through with no sleep.</summary>
    public static bool IsAwake(MetricPoint point) => point.NightStatus == NightSleepStatus.Awake;

    /// <summary>Whether this point is a night still waiting on its morning sync.</summary>
    public static bool IsPending(MetricPoint point) =>
        point.Value is null && point.NightStatus == NightSleepStatus.Pending;

    /// <summary>Whether the sleep tile's figure is from an awake night.</summary>
    public static bool IsAwake(DashboardMetric sleep) => sleep.NightStatus == NightSleepStatus.Awake;

    /// <summary>
    /// Whether last night has yet to arrive — the newest point of the series, which is how the
    /// server says it (<see cref="DashboardMetric.NightStatus"/> is the night of the newest
    /// <em>reading</em>, which is then the night before).
    /// </summary>
    public static bool LastNightPending(DashboardMetric sleep) =>
        sleep.Series.Count > 0 && IsPending(sleep.Series[^1]);

    /// <summary>
    /// How many points at the end of <paramref name="points"/> are nights still waiting to arrive.
    /// Only a trailing run counts: a pending night is by definition the latest, and one stranded
    /// mid-window by later readings is drawn as the gap it has become.
    /// </summary>
    public static int TrailingPending(IReadOnlyList<MetricPoint> points)
    {
        var count = 0;
        for (var i = points.Count - 1; i >= 0 && IsPending(points[i]); i--)
            count++;

        return count;
    }

    /// <summary>
    /// A sleep series with every zero the server left unclassified read as the awake night it is.
    /// </summary>
    /// <remarks>
    /// The alert chart's sleep points carry no <see cref="MetricPoint.NightStatus"/> yet, and the
    /// server's own reading of a bare zero is an awake night (<c>ReadingWindowSummary</c> names a
    /// 0 as <see cref="ReadingFigures.AwakeNight"/>), since that is the only way a night is stored
    /// as 0 minutes. Copies rather than mutates: the points belong to a response other code reads.
    /// A series with nothing to change comes back as itself.
    /// </remarks>
    public static IReadOnlyList<MetricPoint> WithAwakeZeros(IReadOnlyList<MetricPoint> sleep)
    {
        if (!sleep.Any(p => p.Value == 0 && p.NightStatus is null))
            return sleep;

        return sleep
            .Select(p => p.Value == 0 && p.NightStatus is null
                ? new MetricPoint
                {
                    Date = p.Date,
                    Value = p.Value,
                    IsPartial = p.IsPartial,
                    NightStatus = NightSleepStatus.Awake,
                }
                : p)
            .ToList();
    }
}
