namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Generic wearable API client. Each provider implements this interface.
/// </summary>
public interface IDeviceApiClient
{
    Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date);

    /// <summary>
    /// The member's sub-daily series for one civil day — timestamped raw readings for the
    /// granular substrate, alongside (not instead of) the daily snapshot. A device that records
    /// none of the granular metrics returns <see cref="DeviceGranularDay.Empty"/>.
    /// </summary>
    Task<DeviceGranularDay> GetGranularDayAsync(string accessToken, DateOnly date);

    /// <summary>
    /// The wearer's public health-user id — the `users/{user}` segment webhook notifications and
    /// subscriptions are addressed by. Null when the provider does not expose one.
    /// </summary>
    Task<string?> GetHealthUserIdAsync(string accessToken);

    /// <summary>
    /// The wearables paired to the member's provider account — device telemetry (battery level and
    /// status, hardware version), not health data. Requires the
    /// <c>googlehealth.settings.readonly</c> scope; implementations return an empty list rather
    /// than throwing when it was never granted, since a connection authorised before that scope
    /// shipped is a normal state and not a sync failure. Empty for providers that expose no device
    /// registry at all.
    /// </summary>
    Task<IReadOnlyList<PairedDeviceInfo>> GetPairedDevicesAsync(string accessToken);

    /// <summary>
    /// Exercise sessions logged for one civil day, GPS-tagged or not. Requires the
    /// <c>googlehealth.location.readonly</c> scope alongside <c>activity_and_fitness</c> to see
    /// <see cref="ExerciseSession.HasGpsTrack"/> at all — callers check the connection's granted
    /// scopes before calling this, the same as any other optional data type.
    /// </summary>
    Task<IReadOnlyList<ExerciseSession>> GetExerciseSessionsAsync(string accessToken, DateOnly date);

    /// <summary>
    /// One representative GPS fix for a session that <see cref="GetExerciseSessionsAsync"/>
    /// reported as GPS-tagged — the first track point that carries a position. Null when the
    /// session's TCX export carries no usable fix (a GPS lock that failed to acquire is not an
    /// error, just an empty track). Callers use the result for exactly one outbound environmental
    /// lookup and must never persist it — see docs/technical/data_protection_architecture.md.
    /// </summary>
    Task<ExerciseGpsPoint?> GetExerciseGpsPointAsync(string accessToken, string sessionId);
}
