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
    /// The HRV column coalesces like every other metric — a ring that derives it fills the gap a
    /// watch that does not leaves.
    /// </summary>
    [Fact]
    public void Merge_FillsHeartRateVariability_FromWhicheverDeviceReportedIt()
    {
        var watch = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.GooglePixelWatch,
            Date = Date,
            Steps = 5200,
        };
        var ring = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.Oura,
            Date = Date,
            HeartRateVariabilityMs = 31.5m,
        };

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, ring]);

        Assert.NotNull(merged);
        Assert.Equal(5200, merged.Steps);
        Assert.Equal(31.5m, merged.HeartRateVariabilityMs);
    }

    /// <summary>
    /// The stretch and the instant it began are one reading. Taking the length from one device and
    /// the start from another would describe a stretch that never happened, so both come from the
    /// same row by coming through the same coalesce.
    /// </summary>
    [Fact]
    public void Merge_KeepsTheSedentaryStretchAndItsStart_Together()
    {
        var started = new DateTime(2026, 8, 6, 13, 0, 0, DateTimeKind.Utc);
        var watch = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.GooglePixelWatch,
            Date = Date,
            LongestSedentaryStretchMinutes = 240,
            LongestSedentaryStretchStartUtc = started,
            ModerateZoneMinutes = 22,
        };
        var ring = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = MemberId,
            DeviceConnectionId = Guid.NewGuid(),
            DataSource = DeviceType.Oura,
            Date = Date,
            LongestSedentaryStretchMinutes = 90,
            LongestSedentaryStretchStartUtc = started.AddHours(4),
            OvernightBreathingRate = 14.6m,
        };

        var merged = ActivityLogMerge.Merge(MemberId, Date, [watch, ring]);

        Assert.NotNull(merged);
        Assert.Equal(240, merged.LongestSedentaryStretchMinutes);
        Assert.Equal(started, merged.LongestSedentaryStretchStartUtc);
        Assert.Equal(22, merged.ModerateZoneMinutes);
        Assert.Equal(14.6m, merged.OvernightBreathingRate);
    }
}
