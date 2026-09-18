using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The wire names the REST contract uses for hardware brands — <c>fitbit</c>, <c>pixel_watch</c>
/// and the rest — and the mapping back to <see cref="DeviceType"/>.
/// </summary>
/// <remarks>
/// <para>
/// One brand, one wire name. Which data-source API a brand actually connects through is the
/// <c>DeviceProviders</c> configuration's business, not this map's: <c>fitbit</c> and
/// <c>pixel_watch</c> are two entries here and one Google Health block there. That separation is
/// why adding a brand to an API already wired up is a configuration change rather than a code one.
/// </para>
/// <para>
/// <c>apple_health</c> is deliberately absent. Apple exposes no server-side API, so its data only
/// ever arrives through an on-device bridge; naming it here would let it be requested through the
/// server OAuth flow, which has nowhere to send it.
/// </para>
/// <para>
/// Shared rather than private to the connection service because the invite flow resolves the same
/// names from the same requests. Two copies would drift, and the direction they would drift in is a
/// brand that can be invited but not connected.
/// </para>
/// </remarks>
public static class DeviceProviderNames
{
    private static readonly Dictionary<string, DeviceType> ByWireName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fitbit"] = DeviceType.Fitbit,
        ["pixel_watch"] = DeviceType.GooglePixelWatch,
        ["garmin"] = DeviceType.Garmin,
        ["samsung_health"] = DeviceType.GalaxyWatch,
        ["withings"] = DeviceType.Withings,
    };

    private static readonly Dictionary<DeviceType, string> ByDeviceType =
        ByWireName.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Every wire name the contract accepts, for validation messages and validators.</summary>
    public static IReadOnlyCollection<string> All => ByWireName.Keys;

    /// <summary>The brand a wire name names, or false when it names none.</summary>
    public static bool TryResolve(string? provider, out DeviceType deviceType)
    {
        if (provider is not null && ByWireName.TryGetValue(provider, out deviceType))
            return true;

        deviceType = default;
        return false;
    }

    /// <summary>
    /// The wire name for a brand. Brands with no wire name — the on-device-bridge ones, and
    /// <see cref="DeviceType.Other"/> — fall back to their lower-cased enum name so a response can
    /// still describe a connection that predates this map or was made by hand.
    /// </summary>
    public static string ToWireName(DeviceType deviceType) =>
        ByDeviceType.TryGetValue(deviceType, out var name)
            ? name
            : deviceType.ToString().ToLowerInvariant();
}
