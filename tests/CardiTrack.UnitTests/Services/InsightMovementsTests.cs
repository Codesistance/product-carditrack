using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The column round-tripping, and what it does with a row it cannot read.
/// </summary>
public class InsightMovementsTests
{
    [Fact]
    public void AMovementSurvivesTheRoundTrip()
    {
        var written = InsightMovements.Write([Movement(TrackedMetric.Sleep, 5.7m, 7.2m)]);

        var back = Assert.Single(InsightMovements.Read(written));
        Assert.Equal(TrackedMetric.Sleep, back.Kind);
        Assert.Equal("Sleep", back.Metric);
        Assert.Equal("hours a night", back.Unit);
        Assert.Equal(5.7m, back.Recent);
        Assert.Equal(7.2m, back.Usual);
        Assert.Equal(7, back.MeasuredDays);
    }

    [Fact]
    public void TheMetricIsStoredByNameRatherThanByNumber()
    {
        // So reordering TrackedMetric cannot turn every stored sleep movement into a step one.
        Assert.Contains("Sleep", InsightMovements.Write([Movement(TrackedMetric.Sleep, 5.7m, 7.2m)]));
    }

    [Fact]
    public void NothingMovedIsStoredAsNothingAtAll()
    {
        // Null rather than "[]", so an empty list and a column written before this existed read
        // the same way on the way back.
        Assert.Null(InsightMovements.Write([]));
        Assert.Null(InsightMovements.Write(null));
        Assert.Empty(InsightMovements.Read(null));
        Assert.Empty(InsightMovements.Read("  "));
    }

    [Fact]
    public void ARowThatCannotBeReadCostsTheMovementsRatherThanTheDashboard()
    {
        // A row written by a build that knew a metric this one does not must not throw on every
        // read of that member's dashboard. The summary and findings are still there and still
        // worth serving; the next pass overwrites the row regardless.
        Assert.Empty(InsightMovements.Read("{ not json"));
        Assert.Empty(InsightMovements.Read("""[{"kind":"SomethingLater","metric":"?"}]"""));
    }

    private static MetricMovement Movement(TrackedMetric kind, decimal recent, decimal usual) =>
        new(
            kind,
            kind.ToString(),
            "hours a night",
            recent,
            usual,
            Math.Round((recent - usual) / usual * 100m, 0),
            MeasuredDays: 7);
}
