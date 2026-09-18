using QRCoder;

namespace CardiTrack.Mobile.Core.Devices;

/// <summary>
/// Renders an invitation URL as a QR code the wearer can scan off the caregiver's screen.
/// </summary>
/// <remarks>
/// <para>
/// Drawn on the device rather than fetched from the server, which matters for more than a round
/// trip: an image endpoint would be a second place the invitation token has to travel, and it would
/// put that token in a URL the phone's image cache might keep. Encoding locally means the token goes
/// no further than the app that was already handed it.
/// </para>
/// <para>
/// <see cref="PngByteQRCode"/> specifically, out of QRCoder's several renderers, because it is the
/// one with no <c>System.Drawing</c> dependency — it writes the PNG bytes itself. The others pull in
/// desktop graphics stacks that do not exist on Android or iOS.
/// </para>
/// </remarks>
public static class InviteQrCode
{
    /// <summary>
    /// Pixels per QR module. Five keeps a typical invitation URL comfortably scannable from a phone
    /// held at arm's length across a table, at a few kilobytes of PNG.
    /// </summary>
    private const int PixelsPerModule = 5;

    /// <summary>
    /// Error correction level. Quartile — about 25% of the code can be obscured and still read —
    /// because this gets photographed off a glossy screen at an angle, with a reflection across it
    /// as often as not. The cost is a denser code, which the module size above absorbs.
    /// </summary>
    private const QRCodeGenerator.ECCLevel Correction = QRCodeGenerator.ECCLevel.Q;

    /// <summary>
    /// The PNG bytes for a URL, or null when there is nothing to encode. Null rather than an
    /// exception because the caller's alternative is a screen with no code on it, which it has to be
    /// able to render anyway for the moment before the invitation arrives.
    /// </summary>
    public static byte[]? Render(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, Correction);

        return new PngByteQRCode(data).GetGraphic(PixelsPerModule);
    }
}
