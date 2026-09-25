using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Whether a provider grant may be shared with a live connection, so that revoking it would cut
/// that connection off too. One definition for the two places that decide to revoke: the device
/// flows that queue a revocation, and the Worker that carries it out.
/// </summary>
public static class DeviceGrantSharing
{
    /// <summary>
    /// True when another live connection may read through the grant <paramref name="grantAccount"/>
    /// names.
    /// </summary>
    /// <remarks>
    /// Revocation is grant-wide, so this errs towards "shared": a sibling on the same member and
    /// API is taken to share the grant unless both accounts are known and differ — an identity is
    /// best-effort, and a connection whose identity was never captured may well be on the same
    /// account. Beyond the member, only a known match counts; an uncaptured identity there is at
    /// most minutes old, since the connect flow and the first sync both capture it.
    /// </remarks>
    /// <param name="excludingConnectionId">The connection the grant came from, which is not its own sibling.</param>
    /// <param name="deviceType">The grant's brand; siblings are those on the same API.</param>
    /// <param name="grantAccount">The grant's health-user id, when known.</param>
    /// <param name="memberConnections">The member's connections, as read by the caller.</param>
    public static async Task<bool> MayBeSharedAsync(
        IDeviceConnectionRepository connections,
        IReadOnlyCollection<DeviceProviderSettings> providers,
        Guid excludingConnectionId,
        DeviceType deviceType,
        string? grantAccount,
        IEnumerable<DeviceConnection> memberConnections)
    {
        var unprovenSibling = memberConnections.Any(c =>
            c.Id != excludingConnectionId
            && c.IsActive
            && c.ConnectionStatus != ConnectionStatus.Disconnected
            && providers.SameApi(c.DeviceType, deviceType)
            && (grantAccount is null
                || c.HealthUserId is null
                || string.Equals(c.HealthUserId, grantAccount, StringComparison.Ordinal)));

        return unprovenSibling
            || (grantAccount is not null
                && await connections.AnyOtherActiveWithHealthUserIdAsync(excludingConnectionId, grantAccount));
    }
}
