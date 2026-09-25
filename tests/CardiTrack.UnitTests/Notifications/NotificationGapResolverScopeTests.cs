using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// A member-scoped evaluation sees one member's contexts, so it may only reconcile that member's
/// stored rows. Diffing against the caregiver's whole inbox resolved every other member's and every
/// account-level nudge as "gap closed", only for the daily run to reopen (and re-push) them — and
/// the reconnect hand-over runs this path every fifteen minutes while a member stays dark.
/// </summary>
public class NotificationGapResolverScopeTests
{
    [Fact]
    public async Task ResolveForCardiMember_LeavesOtherMembersAndAccountLevelRowsAlone()
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.TokenExpired))
            .Build();
        var memberId = context.Member!.Id;
        var userId = context.User.Id;

        var otherMembersRow = OpenRow(userId, Guid.NewGuid(), "DEVICE_BATTERY_LOW");
        var accountRow = OpenRow(userId, cardiMemberId: null, "TIMEZONE_DEFAULT");

        var snapshots = Substitute.For<INotificationSnapshotQueries>();
        snapshots.BuildContextsForCardiMemberAsync(memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([context]);

        var notifications = Substitute.For<INotificationRepository>();
        notifications.GetForReconciliationAsync(userId, Arg.Any<CancellationToken>())
            .Returns([otherMembersRow, accountRow]);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.Notifications.Returns(notifications);

        await new NotificationGapResolver(unitOfWork, snapshots).ResolveForCardiMemberAsync(memberId);

        Assert.Equal(NotificationState.Open, otherMembersRow.State);
        Assert.Equal(NotificationState.Open, accountRow.State);
        // The member's own gap is still opened.
        await notifications.Received(1).AddRangeAsync(Arg.Is<IEnumerable<Notification>>(rows =>
            rows.Any(n => n.RuleCode == "DEVICE_AUTH_BROKEN" && n.CardiMemberId == memberId)));
    }

    private static Notification OpenRow(Guid userId, Guid? cardiMemberId, string ruleCode) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CardiMemberId = cardiMemberId,
        RuleCode = ruleCode,
        Fingerprint = Guid.NewGuid().ToString("N"),
        State = NotificationState.Open,
        IsActive = true
    };
}
