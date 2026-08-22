using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Domain;

public class ActivityLogMergeTests
{
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 8, 6);

    private static DeviceActivityLog Row(
        DeviceType source = DeviceType.Fitbit,
        int? steps = null,
        int? restingHeartRate = null,
        int? sleepMinutes = null,
        decimal? spO2Average = null,
        int? sedentaryMinutes = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = source,
            Date = Date,
            Steps = steps,
            RestingHeartRate = restingHeartRate,
            SleepMinutes = sleepMinutes,
            SpO2Average = spO2Average,
            SedentaryMinutes = sedentaryMinutes
        };

    [Fact]
    public void Merge_ReturnsNull_WhenThereAreNoRows()
    {
        Assert.Null(ActivityLogMerge.Merge(MemberId, Date, []));
    }

    [Fact]
    public void Merge_TakesTheHigherPriorityValue_WhenBothDevicesReportTheMetric()
    {
        var watch = Row(steps: 8000, restingHeartRate: 65);
        var ring = Row(steps: 7900, restingHeartRate: 63);

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, ring])!;

        Assert.Equal(8000, merged.Steps);
        Assert.Equal(65, merged.RestingHeartRate);
    }

    // The point of merging: a ring contributes sleep and SpO2 the watch never measured.
    [Fact]
    public void Merge_FillsGaps_FromLowerPriorityDevices()
    {
        var watch = Row(steps: 8000, restingHeartRate: 65);
        var ring = Row(sleepMinutes: 420, spO2Average: 96.5m);

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, ring])!;

        Assert.Equal(8000, merged.Steps);
        Assert.Equal(65, merged.RestingHeartRate);
        Assert.Equal(420, merged.SleepMinutes);
        Assert.Equal(96.5m, merged.SpO2Average);
    }

    // Two devices on one body count the same steps; adding them would double-count.
    [Fact]
    public void Merge_NeverSumsCumulativeMetrics()
    {
        var merged = ActivityLogMerge.Merge(MemberId, Date, [Row(steps: 8000), Row(steps: 7900)])!;

        Assert.Equal(8000, merged.Steps);
        Assert.NotEqual(15900, merged.Steps);
    }

    [Fact]
    public void Merge_LeavesMetricNull_WhenNoDeviceReportsIt()
    {
        var merged = ActivityLogMerge.Merge(MemberId, Date, [Row(steps: 8000), Row(steps: 7900)])!;

        Assert.Null(merged.SleepMinutes);
        Assert.Null(merged.VO2Max);
        Assert.Null(merged.StressScore);
    }

    // A genuine zero is a reading, not a gap, so it must win over a lower-priority value.
    [Fact]
    public void Merge_TreatsZeroAsAReading_NotAMissingValue()
    {
        var merged = ActivityLogMerge.Merge(
            MemberId, Date, [Row(sedentaryMinutes: 0), Row(sedentaryMinutes: 600)])!;

        Assert.Equal(0, merged.SedentaryMinutes);
    }

    [Fact]
    public void Merge_RecordsTheHighestPriorityDevice_AsProvenance()
    {
        var watch = Row(source: DeviceType.Fitbit, steps: 8000);
        var ring = Row(source: DeviceType.Oura, sleepMinutes: 420);

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, ring])!;

        Assert.Equal(watch.DeviceConnectionId, merged.DeviceConnectionId);
        Assert.Equal(DeviceType.Fitbit, merged.DataSource);
    }

    [Fact]
    public void Merge_CarriesTheMemberAndDate_OntoTheResult()
    {
        var merged = ActivityLogMerge.Merge(MemberId, Date, [Row(steps: 1)])!;

        Assert.Equal(MemberId, merged.CardiMemberId);
        Assert.Equal(Date, merged.Date);
    }

    // ── ByPriority ───────────────────────────────────────────────────────────────

    [Fact]
    public void ByPriority_PutsThePrimaryDeviceFirst()
    {
        var older = new DeviceConnection { Id = Guid.NewGuid(), ConnectedDate = new DateTime(2026, 1, 1) };
        var primary = new DeviceConnection { Id = Guid.NewGuid(), IsPrimary = true, ConnectedDate = new DateTime(2026, 6, 1) };

        var ordered = ActivityLogMerge.ByPriority([older, primary]).ToList();

        Assert.Equal(primary.Id, ordered[0].Id);
    }

    [Fact]
    public void ByPriority_FallsBackToLongestConnected_WhenNoDeviceIsPrimary()
    {
        var newer = new DeviceConnection { Id = Guid.NewGuid(), ConnectedDate = new DateTime(2026, 6, 1) };
        var older = new DeviceConnection { Id = Guid.NewGuid(), ConnectedDate = new DateTime(2026, 1, 1) };

        var ordered = ActivityLogMerge.ByPriority([newer, older]).ToList();

        Assert.Equal(older.Id, ordered[0].Id);
    }

    // A never-connected device must not outrank one with a real connection date.
    [Fact]
    public void ByPriority_SortsNullConnectedDateLast()
    {
        var connected = new DeviceConnection { Id = Guid.NewGuid(), ConnectedDate = new DateTime(2026, 6, 1) };
        var never = new DeviceConnection { Id = Guid.NewGuid(), ConnectedDate = null };

        var ordered = ActivityLogMerge.ByPriority([never, connected]).ToList();

        Assert.Equal(connected.Id, ordered[0].Id);
    }

    /// <summary>
    /// The 2026-08 columns coalesce like every other metric — a ring that reports HRV and a scale
    /// that reports weight fill each other's gaps on one merged day.
    /// </summary>
    [Fact]
    public void Merge_FillsTheBodyAndRhythmColumns_FromWhicheverDeviceReportedThem()
    {
        var watch = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.GooglePixelWatch,
            Date = Date,
            HeartRateVariabilityMs = 31.5m,
            EcgReadings = 1,
            EcgAtrialFibrillationReadings = 1,
            IrregularRhythmNotifications = 0,
        };
        var scale = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.Withings,
            Date = Date,
            WeightKg = 81.2m,
            BloodGlucoseMin = 66m,
            BloodGlucoseMax = 190m,
        };

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, scale]);

        Assert.NotNull(merged);
        Assert.Equal(31.5m, merged.HeartRateVariabilityMs);
        Assert.Equal(81.2m, merged.WeightKg);
        Assert.Equal(66m, merged.BloodGlucoseMin);
        Assert.Equal(190m, merged.BloodGlucoseMax);
        Assert.Equal(1, merged.EcgAtrialFibrillationReadings);
        // Zero is a reading — the watch looked and the day was quiet — so it wins over the scale's
        // null exactly as a measured zero does for steps.
        Assert.Equal(0, merged.IrregularRhythmNotifications);
    }

    /// <summary>
    /// Rhythm counts are coalesced, never summed: two devices on one wrist-day see the same
    /// episode, and adding them would report one notification as two.
    /// </summary>
    [Fact]
    public void Merge_NeverSumsRhythmCounts_AcrossDevices()
    {
        DeviceActivityLog WithNotifications(DeviceType source, int count) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = source,
            Date = Date,
            IrregularRhythmNotifications = count,
        };

        var merged = ActivityLogMerge.Merge(
            MemberId, Date,
            [WithNotifications(DeviceType.GooglePixelWatch, 1), WithNotifications(DeviceType.Fitbit, 1)]);

        Assert.Equal(1, merged!.IrregularRhythmNotifications);
    }
}
