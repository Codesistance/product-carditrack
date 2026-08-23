using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the raw-samples → hour-vectors arithmetic, and the metric semantics that decide how two
/// readings in one minute combine: additive quantities sum, level measurements take the latest.
/// </summary>
public class GranularDayBucketerTests
{
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly DateTime Hour = new(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);

    private static DeviceGranularDay Day(
        IReadOnlyList<GranularSample>? heartRate = null,
        IReadOnlyList<GranularSample>? steps = null,
        IReadOnlyList<GranularSample>? azm = null,
        IReadOnlyList<GranularSample>? spO2 = null,
        IReadOnlyList<GranularSample>? hrv = null) =>
        new(heartRate ?? [], steps ?? [], azm ?? [], spO2 ?? [], hrv ?? []);

    [Fact]
    public void Bucket_PlacesEachSampleInItsMinuteSlot()
    {
        var day = Day(heartRate:
        [
            new GranularSample(Hour.AddMinutes(5), 72f),
            new GranularSample(Hour.AddMinutes(59), 78f),
        ]);

        var row = Assert.Single(GranularDayBucketer.Bucket(MemberId, ConnectionId, day));

        Assert.Equal(GranularMetric.HeartRate, row.Metric);
        Assert.Equal(Hour, row.HourStartUtc);
        Assert.Equal(72f, row.Values[5]);
        Assert.Equal(78f, row.Values[59]);
        Assert.Null(row.Values[6]);
        Assert.Equal(2, row.SampleCount);
        Assert.Equal(MemberId, row.CardiMemberId);
        Assert.Equal(ConnectionId, row.DeviceConnectionId);
    }

    [Fact]
    public void Bucket_SplitsSamplesAcrossHourRows()
    {
        var day = Day(heartRate:
        [
            new GranularSample(Hour.AddMinutes(59), 70f),
            new GranularSample(Hour.AddMinutes(60), 71f),
        ]);

        var rows = GranularDayBucketer.Bucket(MemberId, ConnectionId, day);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.HourStartUtc == Hour && r.Values[59] == 70f);
        Assert.Contains(rows, r => r.HourStartUtc == Hour.AddHours(1) && r.Values[0] == 71f);
    }

    // AZM arrives once per heart-rate zone, and a minute split across two step intervals is two
    // partial counts of the same minute — both are quantities earned in that minute, so they add.
    [Fact]
    public void Bucket_SumsAdditiveMetrics_LandingInTheSameMinute()
    {
        var day = Day(
            steps: [new GranularSample(Hour, 40f), new GranularSample(Hour.AddSeconds(30), 43f)],
            azm: [new GranularSample(Hour, 1f), new GranularSample(Hour, 2f)]);

        var rows = GranularDayBucketer.Bucket(MemberId, ConnectionId, day);

        Assert.Equal(83f, rows.Single(r => r.Metric == GranularMetric.Steps).Values[0]);
        Assert.Equal(3f, rows.Single(r => r.Metric == GranularMetric.ActiveZoneMinutes).Values[0]);
    }

    // A level has one value at a time — two heart-rate readings in a minute are a re-measurement,
    // not an accumulation, so the chronologically later one stands.
    [Fact]
    public void Bucket_TakesTheLatestLevelReading_InTheSameMinute()
    {
        var day = Day(heartRate:
        [
            new GranularSample(Hour.AddSeconds(40), 75f),
            new GranularSample(Hour.AddSeconds(10), 71f),
        ]);

        var row = Assert.Single(GranularDayBucketer.Bucket(MemberId, ConnectionId, day));

        Assert.Equal(75f, row.Values[0]);
        Assert.Equal(1, row.SampleCount);
    }

    [Fact]
    public void Bucket_ReturnsNothing_ForAnEmptyDay()
    {
        Assert.Empty(GranularDayBucketer.Bucket(MemberId, ConnectionId, DeviceGranularDay.Empty));
    }

    [Fact]
    public void RollupsFromWindow_ComputesPerHourStats_AndSkipsEmptyHours()
    {
        // Two hours of window; heart rate only in the first, steps only in the second.
        var heartRate = new float?[120];
        heartRate[0] = 60f;
        heartRate[1] = 70f;
        heartRate[2] = 80f;
        var steps = new float?[120];
        steps[60] = 50f;
        steps[61] = 30f;

        var window = new GranularWindow
        {
            CardiMemberId = MemberId,
            FromUtc = Hour,
            ToUtc = Hour.AddHours(2),
            MinuteSeries = new Dictionary<GranularMetric, float?[]>
            {
                [GranularMetric.HeartRate] = heartRate,
                [GranularMetric.Steps] = steps,
            },
        };

        var rollups = GranularDayBucketer.RollupsFromWindow(window);

        Assert.Equal(2, rollups.Count);

        var hr = rollups.Single(r => r.Metric == GranularMetric.HeartRate);
        Assert.Equal(Hour, hr.HourStartUtc);
        Assert.Equal(60f, hr.Min);
        Assert.Equal(80f, hr.Max);
        Assert.Equal(70f, hr.Avg);
        Assert.Equal(210f, hr.Sum);
        Assert.Equal(3, hr.SampleCount);

        var stepRollup = rollups.Single(r => r.Metric == GranularMetric.Steps);
        Assert.Equal(Hour.AddHours(1), stepRollup.HourStartUtc);
        Assert.Equal(80f, stepRollup.Sum);
    }
}
