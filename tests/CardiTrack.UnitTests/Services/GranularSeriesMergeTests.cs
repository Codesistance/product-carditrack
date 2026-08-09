using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the minute-grain coalescing rule: first device in priority order with a sample for a
/// minute wins that minute, values are never summed — the same contract
/// <see cref="ActivityLogMerge"/> carries for day rows.
/// </summary>
public class GranularSeriesMergeTests
{
    private static GranularMetricHour Hour(params (int Minute, float Value)[] samples)
    {
        var row = new GranularMetricHour
        {
            DeviceConnectionId = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            Metric = GranularMetric.HeartRate,
            HourStartUtc = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc),
        };
        foreach (var (minute, value) in samples)
            row.Values[minute] = value;
        row.SampleCount = (short)samples.Length;
        return row;
    }

    [Fact]
    public void MergeHour_TheHigherPriorityDeviceWinsAMinute_BothReported()
    {
        var watch = Hour((5, 72f));
        var ring = Hour((5, 99f));

        var merged = GranularSeriesMerge.MergeHour([watch, ring]);

        Assert.Equal(72f, merged[5]);
    }

    [Fact]
    public void MergeHour_DevicesFillEachOthersGaps()
    {
        // The actual benefit of wearing two devices: the ring supplies the minutes the watch
        // missed, without either device's readings being added together.
        var watch = Hour((0, 70f), (2, 74f));
        var ring = Hour((1, 68f), (2, 99f));

        var merged = GranularSeriesMerge.MergeHour([watch, ring]);

        Assert.Equal(70f, merged[0]);
        Assert.Equal(68f, merged[1]);
        Assert.Equal(74f, merged[2]);
        Assert.Null(merged[3]);
    }

    [Fact]
    public void MergeHour_AnEmptyHourMergesToAllNulls()
    {
        var merged = GranularSeriesMerge.MergeHour([]);

        Assert.Equal(GranularMetricHour.MinutesPerHour, merged.Length);
        Assert.All(merged, v => Assert.Null(v));
    }

    [Fact]
    public void MergeHour_ToleratesAShortVector_WithoutThrowing()
    {
        // A malformed row (wrong slot count) must not take the whole window read down; the
        // missing tail simply stays null.
        var malformed = Hour();
        malformed.Values = [61f];

        var merged = GranularSeriesMerge.MergeHour([malformed]);

        Assert.Equal(61f, merged[0]);
        Assert.Null(merged[59]);
    }
}
