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

        // Defaults: linked user, active member, no devices/data/baseline/alerts.
        SetupLink(canViewHealthData: true);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-78)),
            Phone = "+441234567890",
            IsActive = true,
        });
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([]);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns([]);
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

    private void SetupActivityLogs(int days, int steps = 5000, int restingHr = 70,
        int sleepMinutes = 432, int sleepEfficiency = 85)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logs = Enumerable.Range(0, days).Select(offset => new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = today.AddDays(-offset),
            Steps = steps,
            RestingHeartRate = restingHr,
            SleepMinutes = sleepMinutes,
            SleepEfficiency = sleepEfficiency,
        }).ToList();
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);
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
    }

    [Fact]
    public async Task FullData_ComputesChangePercent_Ranges_AndSeries()
    {
        SetupActivityLogs(days: 30, steps: 4250, restingHr: 72, sleepMinutes: 432, sleepEfficiency: 85);
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

        Assert.Equal(72m, metrics.RestingHeartRate.Value);
        Assert.Equal(67, metrics.RestingHeartRate.RangeLow);
        Assert.Equal(74, metrics.RestingHeartRate.RangeHigh);

        Assert.Equal(7.2m, metrics.Sleep.Value);
        Assert.Equal(4, metrics.Sleep.QualityScore);

        Assert.Equal(7, metrics.Steps.Series.Count);
        Assert.All(metrics.Steps.Series, p => Assert.Equal(4250m, p.Value));
        Assert.Equal("green", result.HealthStatus);
        Assert.False(result.Baseline.IsLearning);
    }

    [Fact]
    public async Task LargeDeviation_ColoursMetricOrange()
    {
        SetupActivityLogs(days: 30, steps: 2000);
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
