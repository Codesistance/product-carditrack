using System.Linq.Expressions;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

public class AlertServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IActivityLogRepository _logs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly CardiTrack.Application.Interfaces.Clients.IProfilePhotoStorage _photoStorage =
        Substitute.For<CardiTrack.Application.Interfaces.Clients.IProfilePhotoStorage>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _otherMemberId = Guid.NewGuid();

    /// <summary>
    /// Mid-afternoon, so the day in progress has whole elapsed hours behind it. Pinned rather than
    /// read off the wall clock because <see cref="AlertService"/> fetches different windows in the
    /// first hour of the local day — a real clock made the elapsed-match assertions depend on what
    /// time CI happened to start.
    /// </summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 15, 0, 0, TimeSpan.Zero);

    private readonly FixedTimeProvider _timeProvider = new(Now);

    public AlertServiceTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.ActivityLogs.Returns(_logs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.GranularMetrics.Returns(_granular);
        _unitOfWork.Users.Returns(_users);

        SetupLink(canViewHealthData: true);
        SetupMember(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            EmergencyContactName = "Lorri Warf",
            EmergencyContactPhone = "+441234567891",
            IsActive = true,
        });
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns([]);
        _alerts.CountAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns(0);
        _alerts.CountUnreadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(0);
    }

    // Composed with the real access service, for the same reason DashboardServiceTests is: the
    // link rules being asserted live there, so substituting it away would leave the scoping untested.
    private AlertService CreateSut() =>
        new(_unitOfWork, new CardiMemberAccessService(_unitOfWork), _photoStorage, _timeProvider);

    /// <summary>A SUT on a clock this test moved — used where the hour itself is the subject.</summary>
    private AlertService CreateSutAt(DateTimeOffset now) =>
        new(_unitOfWork, new CardiMemberAccessService(_unitOfWork), _photoStorage, new FixedTimeProvider(now));

    private void SetupMember(params CardiMember[] members) =>
        _members.FindAsync(Arg.Any<Expression<Func<CardiMember, bool>>>())
            .Returns(members.AsEnumerable());

    private void SetupLink(bool canViewHealthData, bool isActive = true, bool isPrimaryCaregiver = false)
    {
        _links.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = isActive,
                CanViewHealthData = canViewHealthData,
                IsPrimaryCaregiver = isPrimaryCaregiver,
            },
        ]);
    }

    private Alert MakeAlert(
        AlertSeverity severity = AlertSeverity.Red,
        AlertType type = AlertType.PatternBreak,
        DateTime? acknowledgedAt = null,
        bool isResolved = false,
        Guid? memberId = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = memberId ?? _memberId,
            AlertType = type,
            Severity = severity,
            Title = "No Movement Detected",
            Message = "Dad hasn't moved this morning.",
            TriggeredDate = Now.UtcDateTime.AddMinutes(-30),
            AcknowledgedDate = acknowledgedAt,
            IsResolved = isResolved,
            IsActive = true,
        };

    [Fact]
    public async Task GetAlerts_ScopesToTheMembersTheUserMayRead()
    {
        await CreateSut().GetAlertsAsync(_userId);

        await _alerts.Received(1).QueryAsync(
            Arg.Is<AlertQuery>(q => q!.CardiMemberIds.Count == 1 && q.CardiMemberIds.Contains(_memberId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAlerts_WithoutAnyLink_QueriesAnEmptyScope()
    {
        _links.GetByUserIdAsync(_userId).Returns([]);

        var result = await CreateSut().GetAlertsAsync(_userId);

        Assert.Empty(result.Alerts);
        await _alerts.Received(1).QueryAsync(
            Arg.Is<AlertQuery>(q => q!.CardiMemberIds.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAlerts_ForAnUnreadableMember_ThrowsRatherThanReturningEmpty()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAlertsAsync(_userId, _otherMemberId));

        // The denial must land before any alert is read, not after.
        await _alerts.DidNotReceive().QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAlerts_WhenTheLinkCannotSeeHealthData_Throws()
    {
        SetupLink(canViewHealthData: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAlertsAsync(_userId, _memberId));
    }

    [Fact]
    public async Task GetAlerts_ClampsLimitToTheDocumentedMaximum()
    {
        await CreateSut().GetAlertsAsync(_userId, limit: 5000, offset: -3);

        await _alerts.Received(1).QueryAsync(
            Arg.Is<AlertQuery>(q => q!.Limit == AlertQuery.MaxLimit && q.Offset == 0),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task GetAlerts_NormalisesDateFiltersToUtc(DateTimeKind kind)
    {
        // TriggeredDate is a timestamptz and the host disables Npgsql's legacy timestamp
        // behaviour, so anything but UTC reaching the provider throws — and the mobile
        // "Today"/"This Week" chips send local midnight.
        var from = DateTime.SpecifyKind(new DateTime(2026, 8, 9, 0, 0, 0), kind);

        await CreateSut().GetAlertsAsync(_userId, from: from, to: from.AddDays(1));

        await _alerts.Received(1).QueryAsync(
            Arg.Is<AlertQuery>(q =>
                q!.From!.Value.Kind == DateTimeKind.Utc && q.To!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAlerts_LocalDateFilter_KeepsTheInstantItNamed()
    {
        var local = DateTime.SpecifyKind(new DateTime(2026, 8, 9, 0, 0, 0), DateTimeKind.Local);

        await CreateSut().GetAlertsAsync(_userId, from: local);

        await _alerts.Received(1).QueryAsync(
            Arg.Is<AlertQuery>(q => q!.From == local.ToUniversalTime()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAlerts_ReadsEveryMemberOnThePageInOneQuery()
    {
        var second = Guid.NewGuid();
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            MakeAlert(),
            MakeAlert(memberId: second),
            MakeAlert(),
        ]);
        SetupMember(
            new CardiMember { Id = _memberId, Name = "Margaret Doe", IsActive = true },
            new CardiMember { Id = second, Name = "Albert Doe", IsActive = true });

        var result = await CreateSut().GetAlertsAsync(_userId);

        Assert.Equal(
            ["Margaret Doe", "Albert Doe", "Margaret Doe"],
            result.Alerts.Select(a => a.CardiMemberName));
        await _members.Received(1).FindAsync(Arg.Any<Expression<Func<CardiMember, bool>>>());
        await _members.DidNotReceive().GetByIdAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task GetAlerts_MapsSeverityTypeAndMemberOntoTheSummary()
    {
        var alert = MakeAlert(AlertSeverity.Orange, AlertType.HeartRate);
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns([alert]);

        var result = await CreateSut().GetAlertsAsync(_userId);

        var summary = Assert.Single(result.Alerts);
        Assert.Equal("orange", summary.Severity);
        Assert.Equal("Heart Rate", summary.Type);
        Assert.Equal("new", summary.Status);
        Assert.Equal("Margaret Doe", summary.CardiMemberName);
        Assert.Equal("+441234567891", summary.EmergencyContactPhone);
    }

    [Fact]
    public async Task GetAlerts_ActivityDecline_AboutDateIsTheQuieterDay_NotTheFiringAfternoon()
    {
        var alert = MakeAlert();
        alert.AlertType = AlertType.Inactivity;
        alert.TriggeredDate = new DateTime(2026, 8, 14, 12, 22, 0, DateTimeKind.Utc);
        alert.MetricValues = """{"rule":"activity_decline","day":"2026-08-13","steps":1477,"baselineAvgSteps":3797}""";
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns([alert]);

        var result = await CreateSut().GetAlertsAsync(_userId);

        var summary = Assert.Single(result.Alerts);
        Assert.Equal(new DateOnly(2026, 8, 13), summary.AboutDate);
        Assert.Equal(alert.TriggeredDate, summary.TriggeredAt);
    }

    [Theory]
    [InlineData(false, false, "new")]
    [InlineData(true, false, "acknowledged")]
    [InlineData(true, true, "resolved")]
    [InlineData(false, true, "resolved")]
    public async Task GetAlerts_DerivesStatusFromAcknowledgementAndResolution(
        bool acknowledged, bool resolved, string expected)
    {
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            MakeAlert(
                acknowledgedAt: acknowledged ? Now.UtcDateTime : null,
                isResolved: resolved),
        ]);

        var result = await CreateSut().GetAlertsAsync(_userId);

        Assert.Equal(expected, Assert.Single(result.Alerts).Status);
    }

    [Fact]
    public async Task GetAlerts_UnreadCountIgnoresTheCallersFilters()
    {
        _alerts.CountUnreadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(4);
        _alerts.CountAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns(1);

        var result = await CreateSut().GetAlertsAsync(_userId, severity: AlertSeverity.Red);

        Assert.Equal(4, result.UnreadCount);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task GetAlerts_ForAMissingMemberRecord_StillReturnsTheAlert()
    {
        SetupMember();
        _alerts.QueryAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns([MakeAlert()]);

        var result = await CreateSut().GetAlertsAsync(_userId);

        var summary = Assert.Single(result.Alerts);
        Assert.Equal(string.Empty, summary.CardiMemberName);
        Assert.Null(summary.EmergencyContactPhone);
    }

    [Fact]
    public async Task Acknowledge_StampsTheUserAndSaves()
    {
        var alert = MakeAlert();
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        var result = await CreateSut().AcknowledgeAsync(_userId, alert.Id);

        Assert.Equal("acknowledged", result.Status);
        Assert.Equal(_userId, alert.AcknowledgedByUserId);
        Assert.NotNull(alert.AcknowledgedDate);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Acknowledge_IsIdempotentAndKeepsTheOriginalAcknowledger()
    {
        var firstResponder = Guid.NewGuid();
        var acknowledgedAt = Now.UtcDateTime.AddMinutes(-10);
        var alert = MakeAlert(acknowledgedAt: acknowledgedAt);
        alert.AcknowledgedByUserId = firstResponder;
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        var result = await CreateSut().AcknowledgeAsync(_userId, alert.Id);

        Assert.Equal(firstResponder, alert.AcknowledgedByUserId);
        Assert.Equal(acknowledgedAt, alert.AcknowledgedDate);
        Assert.Equal(firstResponder, result.AcknowledgedByUserId);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Acknowledge_ForAnAlertOnAnUnreadableMember_Throws()
    {
        var alert = MakeAlert(memberId: _otherMemberId);
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AcknowledgeAsync(_userId, alert.Id));

        Assert.Null(alert.AcknowledgedDate);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Acknowledge_ForAnUnknownAlert_Throws()
    {
        _alerts.GetByIdWithCardiMemberAsync(Arg.Any<Guid>()).Returns((Alert?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AcknowledgeAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Acknowledge_ForASoftDeletedAlert_Throws()
    {
        var alert = MakeAlert();
        alert.IsActive = false;
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AcknowledgeAsync(_userId, alert.Id));
    }

    [Fact]
    public async Task Unacknowledge_ClearsTheStampAndSaves()
    {
        var alert = MakeAlert(acknowledgedAt: Now.UtcDateTime.AddMinutes(-5));
        alert.AcknowledgedByUserId = Guid.NewGuid();
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        var result = await CreateSut().UnacknowledgeAsync(_userId, alert.Id);

        Assert.Equal("new", result.Status);
        Assert.Null(alert.AcknowledgedDate);
        Assert.Null(alert.AcknowledgedByUserId);
        Assert.Null(result.AcknowledgedAt);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Unacknowledge_IsIdempotentForAnAlertNobodyHandled()
    {
        var alert = MakeAlert();
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        var result = await CreateSut().UnacknowledgeAsync(_userId, alert.Id);

        Assert.Equal("new", result.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Resolution is the system's judgement that the underlying condition has passed. A caregiver
    /// undoing their own "handled" must not be able to reopen it.
    /// </summary>
    [Fact]
    public async Task Unacknowledge_ForAResolvedAlert_Refuses()
    {
        var alert = MakeAlert(acknowledgedAt: Now.UtcDateTime.AddHours(-2), isResolved: true);
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<AlertStateException>(
            () => CreateSut().UnacknowledgeAsync(_userId, alert.Id));

        Assert.NotNull(alert.AcknowledgedDate);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Unacknowledge_ForAnAlertOnAnUnreadableMember_Throws()
    {
        var alert = MakeAlert(memberId: _otherMemberId, acknowledgedAt: Now.UtcDateTime);
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().UnacknowledgeAsync(_userId, alert.Id));

        Assert.NotNull(alert.AcknowledgedDate);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Unacknowledge_ForAnUnknownAlert_Throws()
    {
        _alerts.GetByIdWithCardiMemberAsync(Arg.Any<Guid>()).Returns((Alert?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().UnacknowledgeAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_ByThePrimaryCaregiver_SoftDeletesAndSaves()
    {
        SetupLink(canViewHealthData: true, isPrimaryCaregiver: true);
        var alert = MakeAlert();
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await CreateSut().DeleteAsync(_userId, alert.Id);

        Assert.False(alert.IsActive);
        _alerts.Received(1).Update(alert);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Delete_ByANonPrimaryCaregiver_ThrowsAndLeavesTheAlertUntouched()
    {
        // Default link from SetupLink() in the constructor can view but isn't the primary
        // caregiver — Delete needs manage access, a higher bar than Acknowledge's view access.
        var alert = MakeAlert();
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DeleteAsync(_userId, alert.Id));

        Assert.True(alert.IsActive);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Delete_ForAnUnknownAlert_Throws()
    {
        _alerts.GetByIdWithCardiMemberAsync(Arg.Any<Guid>()).Returns((Alert?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DeleteAsync(_userId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_ForAnAlreadySoftDeletedAlert_Throws()
    {
        SetupLink(canViewHealthData: true, isPrimaryCaregiver: true);
        var alert = MakeAlert();
        alert.IsActive = false;
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DeleteAsync(_userId, alert.Id));
    }

    [Fact]
    public async Task GetById_ForAnUnknownAlert_ThrowsBeforeReadingLogs()
    {
        _alerts.GetByIdWithCardiMemberAsync(Arg.Any<Guid>()).Returns((Alert?)null);

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetByIdAsync(_userId, Guid.NewGuid()));
        Assert.Equal("Alert not found", ex.Message);

        await _logs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_ForAnUnreadableMember_ThrowsBeforeReadingLogs()
    {
        var alert = MakeAlert(memberId: _otherMemberId);
        alert.MetricValues = """{"rule":"activity_decline","steps":100}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetByIdAsync(_userId, alert.Id));
        Assert.Equal("Alert not found", ex.Message);

        await _logs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
    }

    /// <summary>
    /// The daily window for the chart, plus the two minute-grain windows the elapsed match needs —
    /// today so far and the same stretch of yesterday. Both, because comparing a running total
    /// against a completed day is the unfairness the match exists to remove.
    /// </summary>
    [Fact]
    public async Task GetById_ForActivityDecline_FetchesTheStepsWindowAndBothElapsedWindows()
    {
        var alert = MakeAlert(type: AlertType.Inactivity);
        alert.MetricValues = """{"rule":"activity_decline","steps":2500,"baselineAvgSteps":5000}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _logs.GetByCardiMemberAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        var detail = await CreateSut().GetByIdAsync(_userId, alert.Id);

        Assert.Equal(alert.Id, detail.AlertId);
        Assert.Equal("activity_decline", detail.Rule);
        // No caregiver link carries a timezone in this fixture, so the anchor clock is UTC.
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        await _logs.Received(1).GetByCardiMemberAndDateRangeAsync(
            _memberId,
            today.AddDays(-(AlertDetailComposer.ActivityDays - 1)),
            today);
        await _granular.Received(2).GetRollupsAsync(
            _memberId, GranularMetric.Steps,
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        // Rollups, never minute vectors: the two stretches reach ~48 hours by late evening and
        // GetWindowAsync would return every metric's full minute grid for them.
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Inside the first hour of the local day there is no whole elapsed hour to compare, so the
    /// rollup ladder is not read at all — the daily chart still is. Asserted explicitly because
    /// this is the hour the wall-clock version of the test above silently swapped itself for.
    /// </summary>
    [Fact]
    public async Task GetById_ForActivityDeclineBeforeTheFirstWholeHour_SkipsTheElapsedMatch()
    {
        var alert = MakeAlert(type: AlertType.Inactivity);
        alert.MetricValues = """{"rule":"activity_decline","steps":2500,"baselineAvgSteps":5000}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _logs.GetByCardiMemberAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        var justAfterMidnight = new DateTimeOffset(2026, 8, 14, 0, 57, 0, TimeSpan.Zero);
        var detail = await CreateSutAt(justAfterMidnight).GetByIdAsync(_userId, alert.Id);

        Assert.Equal("activity_decline", detail.Rule);
        await _logs.Received(1).GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
        await _granular.DidNotReceive().GetRollupsAsync(
            Arg.Any<Guid>(), Arg.Any<GranularMetric>(),
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A sleep alert must not pay for the elapsed match. Last night's sleep is a settled figure by
    /// the time it is reported, so there is no running total to match against.
    /// </summary>
    [Fact]
    public async Task GetById_ForIrregularSleep_FetchesNoGranularWindows()
    {
        var alert = MakeAlert(type: AlertType.Sleep);
        alert.MetricValues = """{"rule":"irregular_sleep","sleepMinutes":240,"baselineAvgSleepMinutes":420}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _logs.GetByCardiMemberAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        await CreateSut().GetByIdAsync(_userId, alert.Id);

        await _granular.DidNotReceive().GetRollupsAsync(
            Arg.Any<Guid>(), Arg.Any<GranularMetric>(),
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_ForDeviceSilence_FetchesNeitherLogsNorGranular()
    {
        var alert = MakeAlert(type: AlertType.Inactivity);
        alert.MetricValues = """{"rule":"device_silence","lastDataUtc":"2026-08-14T08:00:00Z"}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });

        var detail = await CreateSut().GetByIdAsync(_userId, alert.Id);

        Assert.Null(detail.Chart);
        await _logs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(Arg.Any<Guid>(), Arg.Any<int>());
    }

    [Fact]
    public async Task GetById_ForRealtimeHeartRate_FetchesGranularNotDailyLogs()
    {
        var start = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);
        var alert = MakeAlert(type: AlertType.HeartRate);
        alert.MetricValues =
            """{"rule":"realtime_hr","hrTrendLast":90,"windowStartUtc":"2026-08-14T10:00:00Z","windowEndUtc":"2026-08-14T11:00:00Z"}""";
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember { Id = _memberId, Name = "Margaret Doe" });
        _granular.GetWindowAsync(_memberId, start, start.AddHours(1), Arg.Any<CancellationToken>())
            .Returns(new GranularWindow
            {
                CardiMemberId = _memberId,
                FromUtc = start,
                ToUtc = start.AddHours(1),
                MinuteSeries = new Dictionary<GranularMetric, float?[]>
                {
                    [GranularMetric.HeartRate] = [70f, 90f],
                },
            });

        var detail = await CreateSut().GetByIdAsync(_userId, alert.Id);

        Assert.Equal("heartRate", detail.Chart?.Metric);
        await _granular.Received(1).GetWindowAsync(
            _memberId, start, start.AddHours(1), Arg.Any<CancellationToken>());
        await _logs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task GetById_ForASoftDeletedAlert_Throws()
    {
        var alert = MakeAlert();
        alert.IsActive = false;
        _alerts.GetByIdWithCardiMemberAsync(alert.Id).Returns(alert);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetByIdAsync(_userId, alert.Id));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
