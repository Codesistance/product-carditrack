namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a caregiver's history re-pull request can't be taken, with a machine-readable
/// code the API maps to a status. Same shape as <see cref="ManualSyncUnavailableException"/>,
/// and separate for the same reason: none of these are connection faults.
/// </summary>
public class HistoryRepullUnavailableException : Exception
{
    /// <summary>Monitoring is paused, so no data may be collected by any path.</summary>
    public const string MonitoringPaused = "MONITORING_PAUSED";

    /// <summary>The connection is removed, disconnected, or waiting on a token the provider refused.</summary>
    public const string DeviceNotSyncable = "DEVICE_NOT_SYNCABLE";

    /// <summary>A re-pull for this connection is already queued or running.</summary>
    public const string RepullInProgress = "REPULL_IN_PROGRESS";

    /// <summary>This connection was re-pulled too recently. Guards the wearer's provider quota.</summary>
    public const string TooSoon = "REPULL_TOO_SOON";

    public string Code { get; }

    public HistoryRepullUnavailableException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
