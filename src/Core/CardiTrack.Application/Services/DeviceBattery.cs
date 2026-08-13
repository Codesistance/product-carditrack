namespace CardiTrack.Application.Services;

/// <summary>
/// The thresholds that decide when a wearable's battery is worth showing and worth warning about.
/// Shared so the device list and the low-battery rule cannot drift into disagreeing about what
/// "low" means — a tile reading a comfortable 15% beside a notification calling it critical is
/// the kind of contradiction that costs a caregiver their trust in both.
/// </summary>
public static class DeviceBattery
{
    /// <summary>
    /// At or below this percentage the wearable is close enough to stopping that a caregiver
    /// should be told. Ten rather than twenty: a tracker at 20% typically has more than a day
    /// left, and a warning that arrives a day early trains people to ignore it.
    /// </summary>
    public const int LowThresholdPercent = 10;

    /// <summary>The provider band meaning the device is flat and has already stopped reporting.</summary>
    public const string EmptyStatus = "Empty";

    /// <summary>The provider band meaning the device is close to flat.</summary>
    public const string LowStatus = "Low";

    /// <summary>
    /// How long a battery reading stays presentable. Connections are pulled every ten minutes, so
    /// a reading this old means syncing itself has stopped — at which point the number says
    /// nothing about the battery now, and the device list's own stale-sync signalling is the
    /// honest thing to show instead of a percentage frozen a day ago.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    /// <summary>Whether a reading captured at <paramref name="readAtUtc"/> may still be shown.</summary>
    public static bool IsFresh(DateTime? readAtUtc, DateTime utcNow) =>
        readAtUtc is { } read && utcNow - read < FreshFor;

    /// <summary>
    /// Whether a reading warrants warning a caregiver. Either signal alone is enough: some
    /// devices report a band and no percentage, and a device that reports <c>Empty</c> has
    /// already stopped collecting whatever its last percentage said.
    /// </summary>
    public static bool IsLow(int? level, string? status) =>
        level <= LowThresholdPercent
        || string.Equals(status, EmptyStatus, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, LowStatus, StringComparison.OrdinalIgnoreCase);
}
