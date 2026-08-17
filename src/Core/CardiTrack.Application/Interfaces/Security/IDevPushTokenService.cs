using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Security;

/// <summary>
/// Issues and validates the short-lived tokens that authorize the dev-only test-push endpoint
/// (notification_engine.md §13). Same primitive as <see cref="IAckTokenService"/> — HMAC-SHA256
/// over a small tuple plus an expiry — but keyed separately and scoped to a different question:
/// not "did this device really receive delivery X" but "is the caller holding the developer key,
/// and did they ask for exactly this push".
/// </summary>
/// <remarks>
/// Kept a distinct interface rather than a fifth method on <see cref="IAckTokenService"/> because
/// the two must never share a key. The ack key is a production secret injected into every API and
/// Worker instance; this one exists only on a developer's machine, and a compromise of either
/// must not reach the other.
/// </remarks>
public interface IDevPushTokenService
{
    /// <summary>
    /// Mints a token authorizing one test push of exactly this shape. The target and payload shape
    /// are bound into the MAC, so a captured token cannot be re-pointed at another user or
    /// escalated to a category that pushes harder.
    /// </summary>
    string Issue(Guid userId, DeliveryCategory category, AlertSeverity? severity, DateTime expiresAt);

    /// <summary>
    /// False on any forged, expired, malformed, or differently-scoped token. The caller must
    /// surface a bare 401 with no detail about which check failed — a validation oracle would let
    /// a caller without the key narrow down what the endpoint expects.
    /// </summary>
    bool Validate(string token, Guid userId, DeliveryCategory category, AlertSeverity? severity);
}
