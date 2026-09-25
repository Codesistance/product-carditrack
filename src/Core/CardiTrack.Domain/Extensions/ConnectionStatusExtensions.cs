using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Extensions;

public static class ConnectionStatusExtensions
{
    /// <summary>
    /// Whether the provider has refused this connection's credentials, so that only the caregiver
    /// signing the wearer in again (or the auth-recovery probe finding the refusal was temporary)
    /// will bring its readings back.
    /// </summary>
    /// <remarks>
    /// One definition because three places have to agree on it: <c>DEVICE_AUTH_BROKEN</c> fires on
    /// it, and <c>DEVICE_STALE_LONG</c> and the device-silence alert stand down on it. If they
    /// disagreed about a status, a caregiver would be told a watch had gone quiet when the truth
    /// is that it needs reconnecting, or told nothing at all. <see cref="ConnectionStatus.SyncError"/>
    /// is deliberately not one: the grant is intact there and the provider simply failed to answer.
    /// </remarks>
    public static bool NeedsReconnect(this ConnectionStatus status) =>
        status is ConnectionStatus.TokenExpired or ConnectionStatus.AuthError;
}
