using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The key has one job the chart cannot do for itself: say which of its two marks are actually
/// there. Every case here is a chart the composer can genuinely produce.
/// </summary>
public class AlertChartKeyTests
{
    private static AlertChartResponse Sleep(decimal? baseline, MetricReference? reference) => new()
    {
        Metric = "sleep",
        Name = "Sleep",
        Unit = "hours",
        Baseline = baseline,
        Reference = reference,
    };

    private static MetricReference Nsf(decimal high) => new() { Low = 7m, High = high, Source = "NSF" };

    [Fact]
    public void NamesBothMarks_WhenTheChartCarriesBoth()
    {
        var key = AlertChartKey.For(Sleep(3.8m, Nsf(8m)));

        Assert.Equal("Dashed: their usual 3.8  ·  Shaded: recommended 7–8 (NSF)", key);
    }

    /// <summary>
    /// The band is what makes a chronic short sleeper's chart readable — a night plotted against a
    /// dashed 3.8 says nothing about whether 3.8 is anywhere near enough — so the key has to name
    /// it in the published figures, attributed, rather than leave it an unexplained shaded region.
    /// </summary>
    [Fact]
    public void AttributesTheBandToWhoeverPublishesIt()
    {
        Assert.Contains("recommended 7–9 (NSF)", AlertChartKey.For(Sleep(3.8m, Nsf(9m))));
    }

    [Fact]
    public void NamesOnlyTheBaseline_WhenTheMetricHasNoPublishedRange()
    {
        var steps = new AlertChartResponse { Metric = "steps", Baseline = 5432m };

        Assert.Equal("Dashed: their usual 5,432", AlertChartKey.For(steps));
    }

    /// <summary>A member still being learned has no usual to rule, but the published band does not
    /// depend on them and is still worth naming.</summary>
    [Fact]
    public void NamesOnlyTheBand_WhenTheBaselineIsStillLearning()
    {
        Assert.Equal("Shaded: recommended 7–8 (NSF)", AlertChartKey.For(Sleep(null, Nsf(8m))));
    }

    [Fact]
    public void IsNull_WhenTheChartCarriesNeitherMark()
    {
        Assert.Null(AlertChartKey.For(Sleep(null, null)));
    }

    /// <summary>Sleep keeps its tenth of an hour; the whole-number metrics do not grow a false one.</summary>
    [Theory]
    [InlineData("sleep", "3.8")]
    [InlineData("steps", "4")]
    [InlineData("restingHeartRate", "4")]
    public void FormatsEachMetricAtItsOwnPrecision(string metric, string expected)
    {
        var chart = new AlertChartResponse { Metric = metric, Baseline = 3.8m };

        Assert.Contains(expected, AlertChartKey.For(chart));
    }
}
