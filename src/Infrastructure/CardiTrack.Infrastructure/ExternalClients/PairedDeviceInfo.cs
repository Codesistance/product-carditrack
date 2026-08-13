namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// One wearable paired to the wearer's provider account, as reported by the provider's own
/// device registry rather than inferred from the data it uploads.
/// </summary>
/// <remarks>
/// This is device telemetry, not health data: it says something about the hardware doing the
/// measuring, not about the person wearing it. It is fetched from a separate endpoint under a
/// separate scope for exactly that reason, and nothing here is PHI.
/// </remarks>
/// <param name="DeviceType">
/// The provider's device class — <c>TRACKER</c> or <c>SCALE</c>. A scale reports no battery, so
/// this is what distinguishes "no battery to report" from "battery not read".
/// </param>
/// <param name="BatteryLevel">
/// Remaining charge as a percentage (0–100), or null when the provider reports only a band.
/// </param>
/// <param name="BatteryStatus">
/// The provider's coarse band — <c>High</c>, <c>Medium</c>, <c>Low</c> or <c>Empty</c>. Carried
/// verbatim: these are the provider's thresholds against its own hardware, and re-deriving them
/// from <paramref name="BatteryLevel"/> would substitute a guess for a measurement.
/// </param>
/// <param name="DeviceVersion">The product name of the device, e.g. "Charge 6".</param>
/// <param name="LastSyncTimeUtc">
/// When this device last synced to the provider's own mobile app — distinct from
/// <c>DeviceConnection.LastSyncDate</c>, which is when CardiTrack last pulled from the provider.
/// A device can be silent to its phone while CardiTrack keeps successfully re-reading the same
/// stale data, and only this field can tell the two apart.
/// </param>
public sealed record PairedDeviceInfo(
    string? DeviceType,
    int? BatteryLevel,
    string? BatteryStatus,
    string? DeviceVersion,
    DateTime? LastSyncTimeUtc)
{
    /// <summary>
    /// A device class that carries a battery worth reporting. Scales are excluded: they run on
    /// cells that last years, report no level, and would otherwise present as a permanently
    /// unknown battery on a caregiver's device list.
    /// </summary>
    public bool IsBatteryPowered => !string.Equals(DeviceType, "SCALE", StringComparison.OrdinalIgnoreCase);
}
