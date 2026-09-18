namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// How wearer-side device invitations are addressed and how long they live.
/// </summary>
public class DeviceInviteOptions
{
    public const string SectionName = "DeviceInvites";

    /// <summary>
    /// The API's own public origin, e.g. <c>https://api.dev.carditrack.com</c>. Invitation URLs are
    /// built from this.
    /// </summary>
    /// <remarks>
    /// Configured explicitly in every deployed environment, and load-bearing for exactly that
    /// reason. When it is empty the service falls back to the origin of the request that asked for
    /// the invitation, which is right for a developer on localhost and wrong anywhere a Host header
    /// arrives from outside: an attacker who could set it would get CardiTrack to mint an
    /// invitation pointing at their own host, and the caregiver would forward it in good faith.
    /// Dev's Cloud Armor policy already refuses requests whose Host is not the configured domain,
    /// so this is the second of two locks rather than the only one.
    /// </remarks>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How long a shared link lasts. A day, because it has to survive sitting unread in an inbox
    /// overnight — the caregiver who sends it at bedtime should not have to send another at
    /// breakfast.
    /// </summary>
    public int LinkLifetimeMinutes { get; set; } = 24 * 60;

    /// <summary>
    /// How long a QR code lasts. Minutes, because both people are in the room: nothing is waiting on
    /// a delivery, and a code photographed off a screen should stop working long before the
    /// photograph stops existing.
    /// </summary>
    public int QrLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// How long a finished or expired invitation is kept before the Worker deletes it. Thirty days
    /// is enough to answer "why is this device connected" or "did that invitation ever arrive"
    /// during the window anyone asks, and short of the ninety-day floor the audit trail keeps —
    /// which is where the durable record of the same events lives.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Where the privacy policy the wearer's page links to actually lives.
    /// </summary>
    /// <remarks>
    /// Absolute, and configured rather than relative, because it is not on this host: the API serves
    /// no policy page. A relative link would 404 on the one screen in the product where a dead
    /// privacy link matters most — the page asking somebody with no account to share their heart
    /// data.
    /// </remarks>
    public string PrivacyPolicyUrl { get; set; } = "https://carditrack.com/privacy";
}
