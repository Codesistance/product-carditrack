using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Stopping the escalation ladder when a caregiver answers the alert in the app rather than by
/// acknowledging a push.
/// </summary>
/// <remarks>
/// Both doors reach the same conclusion — somebody has this — but until family sharing only the
/// push door closed the ladder. A caregiver who opened the app, read the alert and dealt with it
/// was still escalated against, which in a family of one was invisible and in a family of four is
/// two more people paged about something already handled.
/// </remarks>
public class AckDeliveryServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationDeliveryRepository _deliveries =
        Substitute.For<INotificationDeliveryRepository>();

    private static readonly DateTime UtcNow = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
    private readonly Guid _alertId = Guid.NewGuid();

    public AckDeliveryServiceTests()
    {
        _unitOfWork.NotificationDeliveries.Returns(_deliveries);
    }

    private AckDeliveryService CreateSut() => new(_unitOfWork, new FixedClock(UtcNow));

    private static NotificationDelivery Delivery(DeliveryState state, Guid alertId) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = DeliverySourceType.Alert,
        SourceId = alertId,
        UserId = Guid.NewGuid(),
        Category = DeliveryCategory.Health,
        Severity = AlertSeverity.Red,
        Channel = DeliveryChannel.Push,
        State = state,
        DedupKey = $"alert:{alertId}:{Guid.NewGuid()}",
        ExpiresAt = UtcNow.AddMinutes(25),
        SentDate = state == DeliveryState.Pending ? null : UtcNow.AddMinutes(-4),
        EscalationStage = EscalationStage.Initial
    };

    [Fact]
    public async Task HaltEscalationForAlert_MarksEveryUnfinishedDeliveryAnswered()
    {
        var sent = Delivery(DeliveryState.Sent, _alertId);
        var held = Delivery(DeliveryState.Pending, _alertId);
        _deliveries.GetUnfinishedForAlertAsync(_alertId, Arg.Any<CancellationToken>())
            .Returns([sent, held]);

        var stopped = await CreateSut().HaltEscalationForAlertAsync(_alertId);

        Assert.Equal(2, stopped);
        Assert.Equal(DeliveryState.Answered, sent.State);
        // The deferred copy matters most: left alone it pushes at 06:00 about something dealt
        // with at midnight, which reads as the product not knowing what its own family has done.
        Assert.Equal(DeliveryState.Answered, held.State);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task HaltEscalationForAlert_DoesNotClaimThePushArrived()
    {
        var sent = Delivery(DeliveryState.Sent, _alertId);
        _deliveries.GetUnfinishedForAlertAsync(_alertId, Arg.Any<CancellationToken>()).Returns([sent]);

        await CreateSut().HaltEscalationForAlertAsync(_alertId);

        // Delivered is a claim about a specific handset posting /delivered, and the time-to-ack
        // SLO is measured from exactly those. Counting an in-app answer as one would report a
        // push as having landed on a phone that may have been face-down all night.
        Assert.NotEqual(DeliveryState.Delivered, sent.State);
    }

    [Fact]
    public async Task HaltEscalationForAlert_WithNothingOutstanding_SavesNothing()
    {
        _deliveries.GetUnfinishedForAlertAsync(_alertId, Arg.Any<CancellationToken>()).Returns([]);

        var stopped = await CreateSut().HaltEscalationForAlertAsync(_alertId);

        // The second caregiver answering finds the ladder already stopped. Idempotent, not an error.
        Assert.Equal(0, stopped);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
