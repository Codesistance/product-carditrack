using CardiTrack.Infrastructure.Persistence;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the partition naming and DDL arithmetic. The drop path trusts these names to decide what
/// data is destroyed, so the parse functions are the safety-critical part: anything not matching
/// the scheme must parse as false and therefore never be dropped.
/// </summary>
public class TimeSeriesPartitionsTests
{
    [Fact]
    public void DailyPartitionName_EncodesTheDay()
    {
        Assert.Equal(
            "GranularMetricHours_y20260809",
            TimeSeriesPartitions.DailyPartitionName(new DateOnly(2026, 8, 9)));
    }

    [Fact]
    public void MonthlyPartitionName_EncodesTheMonth()
    {
        Assert.Equal(
            "MetricRollupsHourly_y202608",
            TimeSeriesPartitions.MonthlyPartitionName(new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void CreateDailyPartitionSql_CoversExactlyOneDay_ClosedOpen()
    {
        var sql = TimeSeriesPartitions.CreateDailyPartitionSql(new DateOnly(2026, 12, 31));

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"GranularMetricHours_y20261231\"", sql);
        Assert.Contains("PARTITION OF \"GranularMetricHours\"", sql);
        Assert.Contains("FROM ('2026-12-31') TO ('2027-01-01')", sql);
    }

    [Fact]
    public void CreateMonthlyPartitionSql_CoversExactlyOneMonth_ClosedOpen()
    {
        var sql = TimeSeriesPartitions.CreateMonthlyPartitionSql(new DateOnly(2026, 12, 1));

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"MetricRollupsHourly_y202612\"", sql);
        Assert.Contains("FROM ('2026-12-01') TO ('2027-01-01')", sql);
    }

    [Fact]
    public void TryParseDailyPartition_RoundTrips()
    {
        var day = new DateOnly(2026, 8, 9);

        Assert.True(TimeSeriesPartitions.TryParseDailyPartition(
            TimeSeriesPartitions.DailyPartitionName(day), out var parsed));
        Assert.Equal(day, parsed);
    }

    [Fact]
    public void TryParseMonthlyPartition_RoundTrips()
    {
        var month = new DateOnly(2026, 8, 1);

        Assert.True(TimeSeriesPartitions.TryParseMonthlyPartition(
            TimeSeriesPartitions.MonthlyPartitionName(month), out var parsed));
        Assert.Equal(month, parsed);
    }

    // Never drop what we did not name: the drop path enumerates every child of the parent, so a
    // manually attached partition or a future differently-named child must parse false.
    [Theory]
    [InlineData("GranularMetricHours_default")]
    [InlineData("GranularMetricHours_y2026089")]
    [InlineData("GranularMetricHours_y20261301")]
    [InlineData("SomeOtherTable_y20260809")]
    public void TryParseDailyPartition_RejectsForeignNames(string name)
    {
        Assert.False(TimeSeriesPartitions.TryParseDailyPartition(name, out _));
    }

    [Theory]
    [InlineData("MetricRollupsHourly_default")]
    [InlineData("MetricRollupsHourly_y20260801")]
    [InlineData("MetricRollupsHourly_y202613")]
    public void TryParseMonthlyPartition_RejectsForeignNames(string name)
    {
        Assert.False(TimeSeriesPartitions.TryParseMonthlyPartition(name, out _));
    }

    [Fact]
    public void CreateRealtimePartitionSql_CoversExactlyOneDay_ClosedOpen()
    {
        var sql = TimeSeriesPartitions.CreateRealtimePartitionSql(new DateOnly(2026, 12, 31));

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"RealtimeAssessments_y20261231\"", sql);
        Assert.Contains("PARTITION OF \"RealtimeAssessments\"", sql);
        Assert.Contains("FROM ('2026-12-31') TO ('2027-01-01')", sql);
    }

    [Fact]
    public void TryParseRealtimePartition_RoundTrips()
    {
        var day = new DateOnly(2026, 8, 10);

        Assert.True(TimeSeriesPartitions.TryParseRealtimePartition(
            TimeSeriesPartitions.RealtimePartitionName(day), out var parsed));
        Assert.Equal(day, parsed);
    }

    // The granular and realtime partitions share the daily suffix format; the parsers must
    // still refuse each other's names, or one table's retention could drop the other's data.
    [Theory]
    [InlineData("RealtimeAssessments_default")]
    [InlineData("RealtimeAssessments_y202608")]
    [InlineData("GranularMetricHours_y20260810")]
    public void TryParseRealtimePartition_RejectsForeignNames(string name)
    {
        Assert.False(TimeSeriesPartitions.TryParseRealtimePartition(name, out _));
    }

    [Fact]
    public void TryParseDailyPartition_RejectsARealtimeName()
    {
        Assert.False(TimeSeriesPartitions.TryParseDailyPartition("RealtimeAssessments_y20260810", out _));
    }
}
