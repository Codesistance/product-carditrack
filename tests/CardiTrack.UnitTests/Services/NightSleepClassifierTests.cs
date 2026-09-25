using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The awake rule (decision 2026-09-25): a night the watch was worn through with no sleep recorded
/// is a night the member was awake; no sleep, nothing worn and nothing since is no data; a worn
/// night waits for the morning's sync before it is called either way.
/// </summary>
public class NightSleepClassifierTests
{
    private static readonly DateOnly Night = new(2026, 9, 25);

    [Theory]
    [InlineData(400, 0, true, NightSleepStatus.Slept)]
    [InlineData(0, 0, true, NightSleepStatus.Slept)]
    [InlineData(null, 540, true, NightSleepStatus.Awake)]
    [InlineData(null, 405, true, NightSleepStatus.Awake)]
    [InlineData(null, 404, true, NightSleepStatus.NoData)]
    [InlineData(null, 540, false, NightSleepStatus.Pending)]
    [InlineData(null, 0, true, NightSleepStatus.NoData)]
    [InlineData(null, 0, false, NightSleepStatus.NoData)]
    public void TheStatus_FollowsTheRule(int? sleep, int worn, bool synced, NightSleepStatus expected) =>
        // A 540-minute window: 75% is 405 minutes.
        Assert.Equal(expected, NightSleepClassifier.Classify(sleep, worn, 540, synced, windowOver: true));

    [Fact]
    public void ANightNotOverYet_IsPending_WhateverTheHeartRateSays() =>
        Assert.Equal(
            NightSleepStatus.Pending,
            NightSleepClassifier.Classify(null, 540, 540, syncedSinceWake: false, windowOver: false));

    /// <summary>Until the baseline has learned a bedtime and a waking time, the night runs 22:00
    /// to 07:00 on the member's own clock — here British Summer Time, an hour ahead of UTC.</summary>
    [Fact]
    public void TheDefaultWindow_IsTenToSeven_OnTheMembersClock()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        var (from, to) = NightSleepClassifier.NightWindowUtc(Night, baseline: null, london);

        Assert.Equal(new DateTime(2026, 9, 24, 21, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 9, 25, 6, 0, 0, DateTimeKind.Utc), to);
    }

    /// <summary>A learned bedtime past midnight is the same night, already on the day it ended.</summary>
    [Fact]
    public void ALearnedBedtimePastMidnight_OpensTheWindowOnTheNightsOwnDay()
    {
        var baseline = new PatternBaseline { TypicalBedtime = new TimeOnly(0, 30), TypicalWakeTime = new TimeOnly(8, 15) };

        var (from, to) = NightSleepClassifier.NightWindowUtc(Night, baseline, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 9, 25, 0, 30, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 9, 25, 8, 15, 0, DateTimeKind.Utc), to);
    }

    /// <summary>A learned pair that closes the window — waking before bedtime on the same morning —
    /// falls back to the defaults rather than classify against an empty night.</summary>
    [Fact]
    public void ALearnedPairThatClosesTheWindow_FallsBackToTheDefaults()
    {
        var baseline = new PatternBaseline { TypicalBedtime = new TimeOnly(9, 0), TypicalWakeTime = new TimeOnly(6, 0) };

        var (from, to) = NightSleepClassifier.NightWindowUtc(Night, baseline, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 9, 24, 22, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 9, 25, 7, 0, 0, DateTimeKind.Utc), to);
    }

    [Fact]
    public void Reading_CountsTheWindowsMinutes_AndSeesASyncAfterIt()
    {
        var seriesFrom = new DateTime(2026, 9, 24, 22, 0, 0, DateTimeKind.Utc);
        var window = (seriesFrom.AddMinutes(10), seriesFrom.AddMinutes(20));
        var heartRate = new float?[40];
        for (var minute = 5; minute < 15; minute++)
            heartRate[minute] = 60;

        Assert.Equal((5, false), NightSleepClassifier.Read(heartRate, seriesFrom, window));

        heartRate[30] = 64;
        Assert.Equal((5, true), NightSleepClassifier.Read(heartRate, seriesFrom, window));
    }

    // ── Through the aggregation service ─────────────────────────────────────────

    private readonly IDeviceActivityLogRepository _deviceRows = Substitute.For<IDeviceActivityLogRepository>();
    private readonly IActivityLogRepository _rows = Substitute.For<IActivityLogRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly Guid _memberId = Guid.NewGuid();

    public NightSleepClassifierTests()
    {
        _unitOfWork.GranularMetrics.Returns(_granular);
        _unitOfWork.PatternBaselines.Returns(Substitute.For<IPatternBaselineRepository>());
        // No caregiver with a zone: the member's clock falls back to UTC, and the night to 22:00–07:00 UTC.
        _unitOfWork.UserCardiMembers.Returns(Substitute.For<IUserCardiMemberRepository>());
        _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(_memberId).Returns([]);
    }

    private ActivityLogAggregationService Sut(DateTime utcNow) =>
        new(_deviceRows, _rows, _connections, _unitOfWork, new FakeTimeProvider(utcNow));

    private void StoredRowIs(ActivityLog row) =>
        _rows.GetByCardiMemberAndDateRangeAsync(_memberId, row.Date, row.Date).Returns([row]);

    /// <summary>
    /// Heart rate from 21:00 UTC the evening before, on every minute through
    /// <paramref name="wornUntilUtc"/> and then nothing.
    /// </summary>
    private void HeartRateUntil(DateTime wornUntilUtc) =>
        _granular.GetWindowAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var from = call.ArgAt<DateTime>(1);
                var to = call.ArgAt<DateTime>(2);
                var minutes = new float?[(int)(to - from).TotalMinutes];
                for (var i = 0; i < minutes.Length && from.AddMinutes(i) < wornUntilUtc; i++)
                    minutes[i] = 58;

                return new GranularWindow
                {
                    CardiMemberId = _memberId,
                    FromUtc = from,
                    ToUtc = to,
                    MinuteSeries = new Dictionary<GranularMetric, float?[]> { [GranularMetric.HeartRate] = minutes },
                };
            });

    [Fact]
    public async Task AWornNightWithNoSleep_SyncedSinceWaking_IsAwake_AndCountsAsZero()
    {
        StoredRowIs(new ActivityLog { CardiMemberId = _memberId, Date = Night, SleepMinutes = null });
        HeartRateUntil(new DateTime(2026, 9, 25, 8, 30, 0, DateTimeKind.Utc));

        await Sut(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc)).ClassifyNightAsync(_memberId, Night);

        await _rows.Received().UpsertAsync(Arg.Is<ActivityLog>(l =>
            l.NightStatus == NightSleepStatus.Awake && l.SleepMinutes == 0));
    }

    /// <summary>The heart rate is there, the morning's sync is not: the session may still be coming.</summary>
    [Fact]
    public async Task AWornNightNotSyncedSinceWaking_IsPending()
    {
        StoredRowIs(new ActivityLog { CardiMemberId = _memberId, Date = Night, SleepMinutes = null });
        HeartRateUntil(new DateTime(2026, 9, 25, 6, 30, 0, DateTimeKind.Utc));

        await Sut(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc)).ClassifyNightAsync(_memberId, Night);

        await _rows.Received().UpsertAsync(Arg.Is<ActivityLog>(l =>
            l.NightStatus == NightSleepStatus.Pending && l.SleepMinutes == null));
    }

    [Fact]
    public async Task ANightTheWatchNeverSaw_IsNoData_NotAZero()
    {
        StoredRowIs(new ActivityLog { CardiMemberId = _memberId, Date = Night, SleepMinutes = null });
        HeartRateUntil(new DateTime(2026, 9, 24, 21, 0, 0, DateTimeKind.Utc));

        await Sut(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc)).ClassifyNightAsync(_memberId, Night);

        await _rows.Received().UpsertAsync(Arg.Is<ActivityLog>(l =>
            l.NightStatus == NightSleepStatus.NoData && l.SleepMinutes == null));
    }

    /// <summary>
    /// The re-merge on every sync rebuilds the row from device rows that know nothing of the
    /// night's status; an awake night keeps its status and its 0 through it.
    /// </summary>
    [Fact]
    public async Task ARemerge_KeepsAnAwakeNight()
    {
        var connectionId = Guid.NewGuid();
        _deviceRows.GetByCardiMemberAndDateAsync(_memberId, Night)
            .Returns([new DeviceActivityLog { CardiMemberId = _memberId, DeviceConnectionId = connectionId, Date = Night, Steps = 900 }]);
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([new DeviceConnection { Id = connectionId, CardiMemberId = _memberId }]);
        StoredRowIs(new ActivityLog
        {
            CardiMemberId = _memberId, Date = Night, SleepMinutes = 0, NightStatus = NightSleepStatus.Awake,
        });

        await Sut(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc)).RecomputeAsync(_memberId, Night);

        await _rows.Received().UpsertAsync(Arg.Is<ActivityLog>(l =>
            l.NightStatus == NightSleepStatus.Awake && l.SleepMinutes == 0 && l.Steps == 900));
    }

    /// <summary>A session arriving late is decisive: the night was slept, whatever was said before.</summary>
    [Fact]
    public async Task ALateSession_TurnsAnAwakeNightIntoASleptOne()
    {
        var connectionId = Guid.NewGuid();
        _deviceRows.GetByCardiMemberAndDateAsync(_memberId, Night)
            .Returns([new DeviceActivityLog { CardiMemberId = _memberId, DeviceConnectionId = connectionId, Date = Night, SleepMinutes = 310 }]);
        _connections.GetByCardiMemberIdAsync(_memberId).Returns([new DeviceConnection { Id = connectionId, CardiMemberId = _memberId }]);
        StoredRowIs(new ActivityLog
        {
            CardiMemberId = _memberId, Date = Night, SleepMinutes = 0, NightStatus = NightSleepStatus.Awake,
        });

        await Sut(new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc)).RecomputeAsync(_memberId, Night);

        await _rows.Received().UpsertAsync(Arg.Is<ActivityLog>(l =>
            l.NightStatus == NightSleepStatus.Slept && l.SleepMinutes == 310));
    }
}
