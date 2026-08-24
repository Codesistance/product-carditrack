using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// One point of a chart series. A typed projection of already-fetched member data — never
/// model-generated. See <c>MemberChatService.BuildCharts</c>.
/// </summary>
public sealed record ChartPoint(DateOnly Date, double Value);

/// <summary>
/// One reply series with the comparisons the answer was read against, so the chart can plot them
/// with the values the way the CardiMember Details trends do — the value alone was a line with
/// nothing to mean anything against.
/// </summary>
/// <param name="Baseline">
/// This member's own learned normal for the metric, in the series' own unit, or null while the
/// baseline is still learning. Optional with a default so turns persisted before the field
/// existed deserialise to a chart without a rule rather than failing.
/// </param>
/// <param name="Reference">
/// The published typical-adult band, <b>in the series' own unit</b> — sleep is minutes here
/// because the points are, where <see cref="Services.HealthReferenceRanges.Sleep"/> publishes
/// hours — or null for a metric no standards body publishes one for (steps, overnight HRV;
/// see <see cref="Services.ChatDataRegistry"/> on why those are deliberate).
/// </param>
public sealed record ChartSeries(
    string Metric,
    IReadOnlyList<ChartPoint> Points,
    double? Baseline = null,
    MetricReference? Reference = null);
