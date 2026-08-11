using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Named <c>DeliveryChannel</c> rather than <c>NotificationChannel</c> to avoid reading as the
/// same thing as <c>INotificationChannel</c> (Application/Interfaces/Clients) — that port is the
/// send mechanism (FCM today); this is which of the two delivery surfaces a row targets.
/// </summary>
public enum DeliveryChannel
{
    [Display(Name = "Push")]
    Push = 1,

    [Display(Name = "In-app")]
    InApp = 2
}
