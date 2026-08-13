namespace CardiTrack.Domain.Enums;

/// <summary>
/// The data-source API a device connection syncs through — the OAuth client, REST surface, and
/// keyed engine registration. Deliberately distinct from <see cref="DeviceType"/>, which names the
/// hardware brand on the wearer's wrist: one API can serve several brands (the Google Health API
/// covers Fitbit devices and Pixel Watch alike). Which DeviceTypes map to which api is
/// configuration (<c>DeviceProviders__&lt;i&gt;__DeviceTypes</c>), not code.
/// </summary>
public enum HealthApi
{
    GoogleHealth = 1,
    SamsungHealth = 2,
    GarminConnect = 3,
    AppleHealth = 4,
    Withings = 5,
    Oura = 6,
    Whoop = 7,
}
