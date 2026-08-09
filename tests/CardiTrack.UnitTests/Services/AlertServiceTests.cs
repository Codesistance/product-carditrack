using System.Linq.Expressions;
using CardiTrack.Application.DTOs.Common;
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

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _otherMemberId = Guid.NewGuid();

    public AlertServiceTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.Alerts.Returns(_alerts);

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
    private AlertService CreateSut() => new(_unitOfWork, new CardiMemberAccessService(_unitOfWork));

    private void SetupMember(params CardiMember[] members) =>
        _members.FindAsync(Arg.Any<Expression<Func<CardiMember, bool>>>())
            .Returns(members.AsEnumerable());

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
            TriggeredDate = DateTime.UtcNow.AddMinutes(-30),
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
                acknowledgedAt: acknowledged ? DateTime.UtcNow : null,
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
        var acknowledgedAt = DateTime.UtcNow.AddMinutes(-10);
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
}
