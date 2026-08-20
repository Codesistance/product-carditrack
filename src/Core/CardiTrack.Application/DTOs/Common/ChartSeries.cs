namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// One point of a chart series. A typed projection of already-fetched member data — never
/// model-generated. See <c>MemberChatService.BuildCharts</c>.
/// </summary>
public sealed record ChartPoint(DateOnly Date, double Value);

public sealed record ChartSeries(string Metric, IReadOnlyList<ChartPoint> Points);
