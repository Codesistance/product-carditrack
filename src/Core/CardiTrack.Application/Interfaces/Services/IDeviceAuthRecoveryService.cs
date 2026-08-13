namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Retries the refresh token of connections the provider has refused, so a connection that can
/// come back does so without a caregiver having to notice and reconnect it by hand.
/// </summary>
public interface IDeviceAuthRecoveryService
{
    /// <summary>
    /// Probes every connection due an attempt. Returns how many came back.
    /// </summary>
    Task<int> RecoverDueConnectionsAsync(DateTime utcNow, CancellationToken ct = default);
}
