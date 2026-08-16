using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>
/// Picks the Android channel for a push the app renders itself — the foreground case, where
/// Plugin.Firebase builds a local notification and the payload's
/// <c>android.notification.channel_id</c> (which only the background FCM SDK path reads) never
/// applies. Resolves from the <c>category</c> data key FcmNotificationChannel always sends.
/// </summary>
public static class ForegroundChannelResolver
{
    /// <summary>
    /// A payload with no readable category is treated as Safety: the unrecognised message may be
    /// a safety alert from a newer server, and the failure mode of guessing quiet is a caregiver
    /// who never hears it.
    /// </summary>
    public static string Resolve(IDictionary<string, string>? data)
    {
        var category = data is not null && data.TryGetValue("category", out var raw) ? raw : null;
        return Enum.TryParse<DeliveryCategory>(category, out var parsed) && Enum.IsDefined(parsed)
            ? NotificationChannels.ForCategory(parsed)
            : NotificationChannels.Safety;
    }
}
