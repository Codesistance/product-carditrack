using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A provider grant that is to be ended, held until the provider has confirmed it.
/// </summary>
/// <remarks>
/// <para>
/// Written in the same transaction that discards a connection's tokens — a removed or replaced
/// device, or a grant refused after the code exchange — so the intent to end the grant cannot be
/// lost to a timeout, a cancelled request or a crash between the two. The Worker drains it (see
/// <c>GrantRevocationService</c>), re-checking just before each call that no live connection has
/// since come to read through the same account, because revocation ends the grant for the whole
/// account.
/// </para>
/// <para>
/// The row carries the only remaining copy of the token, encrypted as it was on the connection,
/// and is deleted as soon as the grant is confirmed ended, found shared, or given up on.
/// </para>
/// </remarks>
public class PendingGrantRevocation : BaseEntity
{
    /// <summary>The member the grant was connected for — whose other devices may share it.</summary>
    public Guid CardiMemberId { get; set; }

    /// <summary>The connection it came from; a fresh id for a grant that was never stored.</summary>
    public Guid DeviceConnectionId { get; set; }

    public DeviceType DeviceType { get; set; }

    /// <summary>The provider account, when known — what the shared-grant check compares.</summary>
    public string? HealthUserId { get; set; }

    /// <summary>The encrypted refresh token, or the access token when there was none.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Attempts that did not confirm the grant ended, driving the widening backoff.</summary>
    public int Attempts { get; set; }

    /// <summary>When the Worker should next try. Due immediately on creation.</summary>
    public DateTime NextAttemptAt { get; set; }
}
