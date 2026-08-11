using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which table a <see cref="Entities.NotificationDelivery"/> row actually carries content for.
/// </summary>
/// <remarks>
/// <see cref="Entities.NotificationDelivery"/> is polymorphic over this rather than FK'd to one
/// table, because an <see cref="Entities.Alert"/> and a <see cref="Entities.Notification"/> have
/// nothing else in common — the outbox is the shared reliability substrate, not a shared domain
/// model.
/// </remarks>
public enum DeliverySourceType
{
    [Display(Name = "Alert")]
    Alert = 1,

    [Display(Name = "Notification")]
    Notification = 2
}
