namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Asks for a wearer-side device invitation
/// (POST /api/v1/cardimembers/{id}/device-invites).
/// </summary>
/// <remarks>
/// Note what is not here: no email address, no phone number, no name. The caregiver delivers the
/// link themselves through their own phone's share sheet, so CardiTrack never learns who it went
/// to and never sends anything on their behalf. That is a privacy position as much as a scope one
/// — an address collected here would be a new contact detail about a third party, held to send one
/// message.
/// </remarks>
public class CreateDeviceInviteRequest
{
    /// <summary>
    /// Server-OAuth provider name per the REST contract: fitbit, pixel_watch, garmin,
    /// samsung_health, withings.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// How the caregiver is handing it over — <c>link</c> or <c>qr</c>. This is what sets the
    /// lifetime: a QR code on a screen is good for minutes because both people are in the room,
    /// a shared link for a day because it has to survive an unread inbox.
    /// </summary>
    public string Channel { get; set; } = string.Empty;
}
