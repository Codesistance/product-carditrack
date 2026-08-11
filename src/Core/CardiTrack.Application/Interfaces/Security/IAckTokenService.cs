namespace CardiTrack.Application.Interfaces.Security;

/// <summary>
/// Issues and validates the single-use, stateless tokens that authorize the two anonymous
/// endpoints a push delivery touches: the client's delivered-ack (halts escalation — the highest-
/// value forgery target in the system, §7.2 C3), and the iOS notification service extension's
/// content fetch (§7.2 C5, scoped so it can never be replayed against any other endpoint).
/// </summary>
/// <remarks>
/// Both are the same primitive — HMAC-SHA256 over a small tuple plus an expiry, key in Secret
/// Manager — scoped differently. One interface, not two, since splitting them would be
/// over-abstraction for a single Infrastructure implementation.
/// </remarks>
public interface IAckTokenService
{
    string IssueAckToken(Guid deliveryId, Guid pushDeviceTokenId, DateTime expiresAt);

    /// <summary>
    /// False on any forged, expired, replayed, or other-device token — the caller must treat a
    /// false result as "do nothing, including not halting escalation", never as an error to
    /// surface to the token holder (non-disclosure, matching the alerts 404 convention).
    /// </summary>
    bool ValidateAckToken(string token, Guid deliveryId, out Guid pushDeviceTokenId);

    string IssueFetchToken(Guid deliveryId, Guid pushDeviceTokenId, DateTime expiresAt);

    bool ValidateFetchToken(string token, Guid deliveryId, out Guid pushDeviceTokenId);
}
