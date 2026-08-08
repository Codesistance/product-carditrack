namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a caregiver-triggered device sync can't run, with a machine-readable code the
/// API maps to a status.
/// </summary>
/// <remarks>
/// Separate from <see cref="DeviceConnectionException"/> because none of these are connection
/// faults — the connection is fine, the request is just not one we should serve right now.
/// </remarks>
public class ManualSyncUnavailableException : Exception
{
    /// <summary>Monitoring is paused, so no data may be collected by any path.</summary>
    public const string MonitoringPaused = "MONITORING_PAUSED";

    /// <summary>The member has no active connection to pull from.</summary>
    public const string NoConnectedDevice = "NO_CONNECTED_DEVICE";

    /// <summary>A manual sync ran too recently. Guards the provider's rate limit.</summary>
    public const string TooSoon = "SYNC_TOO_SOON";

    public string Code { get; }

    public ManualSyncUnavailableException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
