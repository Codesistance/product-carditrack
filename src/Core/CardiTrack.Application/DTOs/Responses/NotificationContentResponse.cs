namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// The real content a push payload deliberately omits (§7.1) — what the iOS notification
/// service extension (or Android's data-message handler) fetches over HTTPS, authenticated by
/// the payload's scoped <c>fetchToken</c>, to rewrite the notification before display.
/// </summary>
public class NotificationContentResponse
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
