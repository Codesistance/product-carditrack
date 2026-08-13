namespace CardiTrack.Mobile.Core.Devices;

/// <summary>
/// A hardware brand the app can connect through the server-OAuth flow: the wire name the API
/// contract expects plus the copy the connection screens render. The screens are brand-agnostic —
/// M1-05 picks one of these and M1-06 runs the same PKCE round-trip for any of them, because
/// brands sharing a data-source API (Fitbit and Pixel Watch both ride the Google Health API)
/// share the whole backend flow.
/// </summary>
public sealed record ConnectableDevice(
    string WireName,
    string DisplayName,
    string LogoText,
    string Models,
    string ImageSource)
{
    public static readonly ConnectableDevice Fitbit =
        new("fitbit", "Fitbit", "fitbit", "Charge, Versa, Sense series", "device_fitbit.png");

    // Placeholder tile art until design ships a Pixel Watch asset (no Figma slot yet).
    public static readonly ConnectableDevice PixelWatch =
        new("pixel_watch", "Google Pixel Watch", "Pixel", "Pixel Watch 1–3", "device_other.png");
}
