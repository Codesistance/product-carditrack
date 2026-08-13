using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class DashboardServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IRealtimeAssessmentRepository _realtimeAssessments = Substitute.For<IRealtimeAssessmentRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public DashboardServiceTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.RealtimeAssessments.Returns(_realtimeAssessments);

        // Defaults: linked user, active member, no devices/data/baseline/alerts.
        SetupLink(canViewHealthData: true);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
            Phone = "+441234567890",
            EmergencyContactName = "Lorri Warf",
            EmergencyContactPhone = "+441234567891",
            IsActive = true,
        });
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([]);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns([]);
        _realtimeAssessments.GetLatestAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns((RealtimeAssessment?)null);
    }

    // Composed with the real access service rather than a stub: the link rules under test here
    // (active + CanViewHealthData) now live in CardiMemberAccessService, so substituting it away
    // would leave nothing asserting that the dashboard is actually gated.
    private DashboardService CreateSut() => new(_unitOfWork, new CardiMemberAccessService(_unitOfWork));

    private void SetupLink(bool canViewHealthData, bool isActive = true)
    {
        _links.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = isActive,
                CanViewHealthData = canViewHealthData,
            },
        ]);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <param name="endingDaysAgo">
    /// Where the newest day sits. 0 means the history runs up to the day in progress, which is
    /// what ingestion produces now; 1 builds a history of completed days only.
    /// </param>
    private void SetupActivityLogs(int days, int steps = 5000, int restingHr = 70,
        int sleepMinutes = 432, int sleepEfficiency = 85, int endingDaysAgo = 0)
    {
        var newest = Today.AddDays(-endingDaysAgo);
        var logs = Enumerable.Range(0, days).Select(offset => new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = newest.AddDays(-offset),
            Steps = steps,
            RestingHeartRate = restingHr,
            SleepMinutes = sleepMinutes,
            SleepEfficiency = sleepEfficiency,
        }).ToList();
        SetupActivityLogs(logs);
    }

    private void SetupActivityLogs(List<ActivityLog> logs) =>
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);

    // ── Provisional baselines ───────────────────────────────────────────────────
    //
    // Windows are tried longest-first (30, 14, 7): the established picture when it exists, else
    // the best provisional one, so the dashboard can colour metrics from about the first week
    // instead of sitting in "learning" until day 30.

    [Fact]
    public async Task FallsBackToAProvisionalBaseline_WhenNoEstablishedOneExists()
    {
        SetupActivityLogs(days: 8);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 7).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 7,
            AvgSteps = 5000,
            AvgRestingHeartRate = 70,
            AvgSleepMinutes = 432,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.False(result.Baseline.IsLearning);
        Assert.True(result.Baseline.IsProvisional);
        Assert.Equal(7, result.Baseline.BaselinePeriodDays);
        // The colours anchor to the provisional average instead of reading "unknown".
        Assert.Equal("green", result.Metrics!.RestingHeartRate.Status);
    }

    [Fact]
    public async Task PrefersTheEstablishedBaseline_AndNeverAsksForProvisionalOnes()
    {
        SetupActivityLogs(days: 30);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgRestingHeartRate = 70,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.False(result.Baseline.IsProvisional);
        Assert.Equal(30, result.Baseline.BaselinePeriodDays);
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(_memberId, 14);
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(_memberId, 7);
    }

    [Fact]
    public async Task PrefersTheFourteenDayWindow_OverTheSevenDay()
    {
        SetupActivityLogs(days: 15);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 14).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 14,
            AvgRestingHeartRate = 70,
        });
        _baselines.GetLatestByCardiMemberAsync(_memberId, 7).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 7,
            AvgRestingHeartRate = 99,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(14, result.Baseline.BaselinePeriodDays);
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(_memberId, 7);
    }

    [Fact]
    public async Task StaysLearning_WhenNoWindowHasABaselineYet()
    {
        SetupActivityLogs(days: 3);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.True(result.Baseline.IsLearning);
        Assert.False(result.Baseline.IsProvisional);
        Assert.Null(result.Baseline.BaselinePeriodDays);
    }

    // Both numbers are returned side by side: Phone powers the dashboard's plain Call/Message
    // actions, EmergencyContactPhone powers the separate, visually distinct Emergency Call tile
    // (issue #162) — the two are never conflated.
    [Fact]
    public async Task ReturnsBoth_TheMembersOwnPhone_AndTheEmergencyContact()
    {
        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal("+441234567890", result.Phone);
        Assert.Equal("+441234567891", result.EmergencyContactPhone);
        Assert.Equal("Lorri Warf", result.EmergencyContactName);
    }

    [Fact]
    public async Task Throws_WhenUserHasNoLinkToMember()
    {
        _links.GetByUserIdAsync(_userId).Returns([]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetDashboardAsync(_userId, _memberId));
    }

    [Fact]
    public async Task Throws_WhenLinkForbidsHealthData()
    {
        SetupLink(canViewHealthData: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetDashboardAsync(_userId, _memberId));
    }

    [Fact]
    public async Task Throws_WhenMemberInactive()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, IsActive = false });

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetDashboardAsync(_userId, _memberId));
    }

    [Fact]
    public async Task NoDeviceAndNoData_ReturnsNullMetrics_AndUnknownStatus()
    {
        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.False(result.Device.HasActiveConnection);
        Assert.Null(result.Metrics);
        Assert.Equal("unknown", result.HealthStatus);
        Assert.True(result.Baseline.IsLearning);
        Assert.Equal(0, result.Baseline.DaysCaptured);
        Assert.Equal(78, result.Age);
    }

    [Fact]
    public async Task DataWithoutBaseline_IsLearning_WithNullChangePercent()
    {
        SetupActivityLogs(days: 10);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.True(result.Baseline.IsLearning);
        Assert.Equal(10, result.Baseline.DaysCaptured);
        Assert.Equal(33, result.Baseline.PercentComplete);
        Assert.NotNull(result.Metrics);
        Assert.Equal(5000m, result.Metrics!.Steps.Value);
        Assert.Null(result.Metrics.Steps.ChangePercent);
        Assert.Equal("unknown", result.Metrics.Steps.Status);
        Assert.Equal("unknown", result.HealthStatus);
        // No usual day yet means no denominator at all. This used to be a flat 10,000 — a figure
        // from a 1965 pedometer's brand name, which is exactly the invention HealthReferenceRanges
        // refuses to make for steps. The clients draw no bar rather than measure a member against
        // a number nobody published.
        Assert.Null(result.Metrics.Steps.Goal);
    }

    [Fact]
    public async Task FullData_ComputesChangePercent_Ranges_AndSeries()
    {
        // Ending yesterday, so the steps reading covers a finished day and is comparable against
        // a whole-day average. See TodaysSteps_... below for the day still in progress.
        SetupActivityLogs(days: 30, steps: 4250, restingHr: 72, sleepMinutes: 432, sleepEfficiency: 85,
            endingDaysAgo: 1);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
            AvgRestingHeartRate = 70,
            StdDevHeartRate = 3.5m,
            AvgSleepMinutes = 450,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);
        var metrics = result.Metrics!;

        Assert.Equal(-15m, metrics.Steps.ChangePercent);
        Assert.Equal("green", metrics.Steps.Status);
        Assert.Equal(5000m, metrics.Steps.Goal);
        // Every rated card carries its star rating out to the client, not just sleep.
        Assert.Equal(4, metrics.Steps.QualityScore);

        Assert.Equal(72m, metrics.RestingHeartRate.Value);
        Assert.Equal(67, metrics.RestingHeartRate.RangeLow);
        Assert.Equal(74, metrics.RestingHeartRate.RangeHigh);
        Assert.Equal(5, metrics.RestingHeartRate.QualityScore);

        Assert.Equal(7.2m, metrics.Sleep.Value);
        Assert.Equal(4, metrics.Sleep.QualityScore);

        // The series always runs to today, so the day this member has no row for yet is a gap
        // rather than a missing point.
        Assert.Equal(MemberInsightsCalculator.SeriesDays, metrics.Steps.Series.Count);
        Assert.Equal(Today, metrics.Steps.Series[^1].Date);
        Assert.Null(metrics.Steps.Series[^1].Value);
        Assert.All(metrics.Steps.Series.SkipLast(1), p => Assert.Equal(4250m, p.Value));
        Assert.Equal("green", result.HealthStatus);
        Assert.False(result.Baseline.IsLearning);
    }

    [Fact]
    public async Task LargeDeviation_ColoursMetricOrange()
    {
        SetupActivityLogs(days: 30, steps: 2000, endingDaysAgo: 1);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(-60m, result.Metrics!.Steps.ChangePercent);
        Assert.Equal("orange", result.Metrics.Steps.Status);
    }

    // ── The day in progress (ingestion now stores it) ────────────────────────────────────────

    // The whole point of pulling today: the caregiver's Key Metrics have to move during the day
    // rather than sitting on a completed day until midnight.
    [Fact]
    public async Task TodaysRow_IsWhatTheMetricsReport()
    {
        SetupActivityLogs(
        [
            new ActivityLog { CardiMemberId = _memberId, Date = Today, Steps = 1200 },
            new ActivityLog { CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 9000 },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(1200m, result.Metrics!.Steps.Value);
    }

    // Steps accumulate through the day, so scoring a part-finished day against a whole-day
    // average would report every member alive as collapsing every morning.
    [Fact]
    public async Task TodaysSteps_AreNotScoredAgainstTheWholeDayBaseline()
    {
        SetupActivityLogs(days: 30, steps: 1200);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(1200m, result.Metrics!.Steps.Value);
        Assert.Null(result.Metrics.Steps.ChangePercent);
        Assert.Equal("unknown", result.Metrics.Steps.Status);
        // The goal is what carries a partial day, and it is honest about being partway through.
        Assert.Equal(5000m, result.Metrics.Steps.Goal);
    }

    // Resting heart rate is a daily summary the provider either has or does not — not a running
    // total — so a today reading is a whole reading and stays comparable.
    [Fact]
    public async Task TodaysRestingHeartRate_IsStillScoredAgainstTheBaseline()
    {
        SetupActivityLogs(days: 30, restingHr: 84);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgRestingHeartRate = 70,
        });

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(84m, result.Metrics!.RestingHeartRate.Value);
        Assert.Equal(20m, result.Metrics.RestingHeartRate.ChangePercent);
    }

    // Temperature carries its own device-derived baseline per day (Google Health computes it),
    // not our BaselineCalculationWorker's PatternBaseline — so it's a real comparison even
    // though no PatternBaseline is set up in this test.
    [Fact]
    public async Task Temperature_ComparesAgainstItsOwnDeviceBaseline_NotPatternBaseline()
    {
        SetupActivityLogs(
        [
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Today,
                Temperature = 33.8m,
                TemperatureBaseline = 33.5m,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(33.8m, result.Metrics!.Temperature.Value);
        Assert.Equal(33.5m, result.Metrics.Temperature.Baseline);
        Assert.NotNull(result.Metrics.Temperature.ChangePercent);
    }

    // A clinically meaningful skin-temperature shift is a tiny percentage of a ~33-37°C
    // baseline, so the shared 30%/50% percent thresholds would read every real deviation as
    // "green". Temperature instead compares against the device's own per-day stddev.
    [Theory]
    [InlineData(33.5, 0.2, "green")]  // 0.5 stddev
    [InlineData(33.5, 0.5, "yellow")] // 1.25 stddev
    [InlineData(33.5, 1.0, "orange")] // 2.5 stddev
    public async Task Temperature_ScoresDeviationAgainstItsOwnStdDev_NotPercent(
        decimal baseline, decimal deltaFromBaseline, string expectedStatus)
    {
        SetupActivityLogs(
        [
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Today,
                Temperature = baseline + deltaFromBaseline,
                TemperatureBaseline = baseline,
                TemperatureVariation = 0.4m,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(expectedStatus, result.Metrics!.Temperature.Status);
    }

    [Fact]
    public async Task Temperature_FallsBackToPercentThresholds_WhenNoVariationIsReported()
    {
        // Some devices/days won't have a variation figure — the percent-based BuildMetric result
        // stands rather than throwing or leaving Status unset.
        SetupActivityLogs(
        [
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Today,
                Temperature = 33.8m,
                TemperatureBaseline = 33.5m,
                TemperatureVariation = null,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal("green", result.Metrics!.Temperature.Status);
    }

    // No baseline concept exists for SpO2 yet — the value is shown without a trend judgement.
    [Fact]
    public async Task SpO2_HasNoBaselineComparison_StatusStaysUnknown()
    {
        SetupActivityLogs(
        [
            new ActivityLog { CardiMemberId = _memberId, Date = Today, SpO2Average = 97.2m },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal(97.2m, result.Metrics!.SpO2.Value);
        Assert.Null(result.Metrics.SpO2.ChangePercent);
        Assert.Equal("unknown", result.Metrics.SpO2.Status);
    }

    // Today's row appears as soon as the provider reports anything, and providers populate
    // metrics unevenly. Each card falls back to the last day that actually carried its metric
    // rather than all three blanking because today's row is only partly filled in.
    [Fact]
    public async Task MetricsMissingFromTodaysRow_FallBackToTheLastDayThatHadThem()
    {
        SetupActivityLogs(
        [
            new ActivityLog { CardiMemberId = _memberId, Date = Today, Steps = 1200 },
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Today.AddDays(-1),
                Steps = 9000,
                RestingHeartRate = 68,
                SleepMinutes = 432,
                SleepEfficiency = 91,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);
        var metrics = result.Metrics!;

        Assert.Equal(1200m, metrics.Steps.Value);
        Assert.Equal(68m, metrics.RestingHeartRate.Value);
        Assert.Equal(7.2m, metrics.Sleep.Value);
        // Read off the same night as the duration, never a different one.
        Assert.Equal(5, metrics.Sleep.QualityScore);
    }

    [Fact]
    public async Task UnresolvedAlerts_DriveHealthStatusAndUnreadCount()
    {
        SetupActivityLogs(days: 30);
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.Inactivity,
                Severity = AlertSeverity.Red,
                Title = "Unusual inactivity",
                Message = "No steps recorded today",
                IsResolved = false,
            },
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.Sleep,
                Severity = AlertSeverity.Yellow,
                Title = "Restless night",
                Message = "Sleep efficiency below usual",
                IsResolved = false,
                AcknowledgedDate = DateTime.UtcNow,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Equal("red", result.HealthStatus);
        Assert.Equal(1, result.UnreadAlertCount);
        Assert.Equal(2, result.RecentAlerts.Count);
        Assert.Equal("red", result.RecentAlerts[0].Severity);
        Assert.False(result.RecentAlerts[0].IsAcknowledged);
        Assert.True(result.RecentAlerts[1].IsAcknowledged);
    }

    [Fact]
    public async Task DeviceState_ReflectsPrimaryConnection()
    {
        var lastSync = DateTime.UtcNow.AddMinutes(-10);
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns(
        [
            new DeviceConnection
            {
                CardiMemberId = _memberId,
                DeviceType = DeviceType.Fitbit,
                DeviceName = "Mom's Fitbit",
                IsPrimary = true,
                ConnectionStatus = ConnectionStatus.Connected,
                LastSyncDate = lastSync,
            },
        ]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.True(result.Device.HasActiveConnection);
        Assert.Equal("Mom's Fitbit", result.Device.DeviceName);
        Assert.Equal("Connected", result.Device.ConnectionStatus);
        Assert.Equal(lastSync, result.LastSyncedAt);
    }

    [Fact]
    public async Task Dashboard_ReturnsNullLastSynced_WhenMemberHasNoDevices()
    {
        // A member with no paired device is a supported state, so LastSyncedAt resolves via
        // Max over an empty sequence. With a nullable selector that yields null rather than
        // throwing — pinned here because it reads like a bug.
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([]);

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.Null(result.LastSyncedAt);
        Assert.False(result.Device.HasActiveConnection);
    }

    // ── Monitoring pause (M1-13) ────────────────────────────────────────────────

    private void SetupPausedMember(DateTime? pausedUntil, string? reason = null) =>
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
            MonitoringPausedUntil = pausedUntil,
            MonitoringPauseReason = reason,
            IsActive = true,
        });

    [Fact]
    public async Task Dashboard_ReportsPausedStatus_RatherThanAHealthColour()
    {
        SetupPausedMember(DateTime.UtcNow.AddHours(12), "Travelling");

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        // A paused member is not being watched — showing green would say we looked and
        // everything was fine.
        Assert.Equal("paused", result.HealthStatus);
        Assert.True(result.MonitoringPaused);
        Assert.Equal("Travelling", result.MonitoringPauseReason);
    }

    [Fact]
    public async Task Dashboard_IgnoresElapsedPause()
    {
        SetupPausedMember(DateTime.UtcNow.AddMinutes(-1), "Travelling");

        var result = await CreateSut().GetDashboardAsync(_userId, _memberId);

        Assert.False(result.MonitoringPaused);
        Assert.NotEqual("paused", result.HealthStatus);
        Assert.Null(result.MonitoringPauseReason);
    }
}
