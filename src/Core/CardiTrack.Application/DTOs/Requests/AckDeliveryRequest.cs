namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// POST /api/v1/notifications/{id}/delivered. Posted from the background push handler, before
/// any user interaction — <c>[AllowAnonymous]</c>, authorized by <see cref="AckToken"/> rather
/// than a session (notification_engine.md §7.2 C3).
/// </summary>
public class AckDeliveryRequest
{
    public string AckToken { get; set; } = string.Empty;
}
