using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// Tells the wearable provider that a grant is over, so the token dies at their end rather than
/// merely being forgotten at ours.
/// </summary>
/// <remarks>
/// <para>
/// Discarding a refresh token is not revoking it. Until this call is made the grant stays live at
/// Google: CardiTrack still appears in the wearer's list of apps with access to their health data,
/// and anyone holding a copy of that token — from a backup, a log, a leaked database — can still
/// exchange it for readings. The published policy says revoking access deletes what was collected;
/// a caregiver disconnecting a device reasonably reads that as access ending, and it has to.
/// </para>
/// <para>
/// <strong>Best effort, deliberately.</strong> A provider outage must not stop a caregiver
/// disconnecting a device, removing a member, or having their account erased — those are the
/// user's decisions and they are irreversible on our side whatever Google says. So this never
/// throws for a provider failure; it reports whether the grant is confirmed gone, and the caller
/// decides what an unconfirmed revocation is worth saying about.
/// </para>
/// </remarks>
public interface IOAuthGrantRevoker
{
    /// <summary>
    /// Revokes this connection's grant at the provider.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the provider has confirmed the grant is gone — including when it reports
    /// the token as already invalid, which is the same outcome by a different route.
    /// <c>false</c> when it could not be confirmed: no token stored, no revocation endpoint
    /// configured for this device type, or the call failed. A <c>false</c> is a grant that may
    /// still be live and that nothing will retry, because the row is usually deleted moments
    /// later — callers should say so rather than swallow it.
    /// </returns>
    Task<bool> TryRevokeAsync(DeviceConnection connection, CancellationToken ct = default);
}
