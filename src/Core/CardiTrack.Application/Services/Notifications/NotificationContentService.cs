using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

public interface INotificationContentService
{
    /// <summary>Null if the delivery doesn't exist — the controller turns that into a 404, non-disclosure per the alerts convention.</summary>
    Task<NotificationContentResponse?> GetContentAsync(Guid deliveryId, CancellationToken ct = default);
}

/// <summary>
/// Resolves the real title/body a content-free payload omitted (§7.1). Only <see
/// cref="DeliverySourceType.Alert"/> gets genuinely richer content here — <see
/// cref="Domain.Entities.Alert.Title"/>/<c>.Message</c> are server-rendered English text. A
/// <see cref="DeliverySourceType.Notification"/> row stores localization <em>keys</em>
/// (<c>TitleKey</c>/<c>BodyKey</c>), not rendered copy — resolving those into words is a client
/// capability today (<c>NudgeCopy</c> in <c>CardiTrack.Mobile.Core</c>), so this falls back to
/// the same category-level teaser the content-free payload already carries rather than
/// duplicating that resolution table server-side for the two Safety-class nudge rules that push.
/// </summary>
public class NotificationContentService : INotificationContentService
{
    private readonly IUnitOfWork _unitOfWork;

    public NotificationContentService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<NotificationContentResponse?> GetContentAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var delivery = await _unitOfWork.NotificationDeliveries.GetByIdAsync(deliveryId);
        if (delivery is null)
            return null;

        if (delivery.SourceType == DeliverySourceType.Alert)
        {
            var alert = await _unitOfWork.Alerts.GetByIdAsync(delivery.SourceId);
            if (alert is not null)
                return new NotificationContentResponse { Title = alert.Title, Body = alert.Message };
        }

        var (title, body) = PushTeaser.For(delivery.Category, delivery.Severity, delivery.AlertType);
        return new NotificationContentResponse { Title = title, Body = body };
    }
}
