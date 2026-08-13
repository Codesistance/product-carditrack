using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Who is allowed to act on a notification, as opposed to merely see it. The inbox deliberately
/// returns a relative's rows so the rest of a family can tell something is outstanding, and the
/// card hides the buttons — but hiding buttons is not authorisation, so the service has to refuse
/// the call as well.
/// </summary>
public class NotificationOwnershipTests
{
    private static readonly Guid Relative = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid NotificationId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static Notification Row(bool isOwner) => new()
    {
        Id = NotificationId,
        OrganizationId = Guid.NewGuid(),
        // Carried on the relative's own inbox: this is their row, they are simply not the owner.
        UserId = Relative,
        RuleCode = MedicalNotesEmptyRule.Code,
        RuleVersion = 1,
        Category = NotificationCategory.Unlock,
        Priority = NotificationPriority.Low,
        Fingerprint = "family-row",
        TitleKey = "nudge.notes.title",
        BodyKey = "nudge.notes.body",
        BenefitKey = "nudge.notes.benefit",
        TemplateData = "{}",
        ActionDeepLink = "carditrack://settings",
        State = NotificationState.Open,
        IsOwner = isOwner
    };

    private static NotificationService ServiceReturning(Notification row)
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.Notifications.GetByIdAsync(NotificationId).Returns(row);

        return new NotificationService(unitOfWork, new NoOpNotificationGapResolver());
    }

    [Fact]
    public async Task ARelativeCannotSnoozeARowTheyDoNotOwn()
    {
        var service = ServiceReturning(Row(isOwner: false));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SnoozeAsync(Relative, NotificationId, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task ARelativeCannotDismissARowTheyDoNotOwn()
    {
        var service = ServiceReturning(Row(isOwner: false));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DismissAsync(Relative, NotificationId, acknowledgedConsequence: true));
    }

    /// <summary>
    /// The seen clock is the comply funnel's denominator, and the unseen badge already counts owned
    /// rows only. A relative opening the inbox must not start that clock on someone else's behalf.
    /// </summary>
    [Fact]
    public async Task ARelativeCannotMarkARowTheyDoNotOwnAsSeen()
    {
        var service = ServiceReturning(Row(isOwner: false));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.MarkSeenAsync(Relative, NotificationId));
    }

    /// <summary>The same call on the owner's copy has to keep working, or the guard is just an outage.</summary>
    [Fact]
    public async Task TheOwnerIsStillAllowedToSnooze()
    {
        var row = Row(isOwner: true);
        var service = ServiceReturning(row);

        await service.SnoozeAsync(Relative, NotificationId, TimeSpan.FromDays(7));

        Assert.Equal(NotificationState.Snoozed, row.State);
    }
}
