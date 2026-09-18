using CardiTrack.Mobile.Core.Devices;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The QR code the wearer scans off the caregiver's screen.
/// </summary>
/// <remarks>
/// These assert that it renders and that it is a PNG, not that the modules are in the right places —
/// that is QRCoder's job and it has its own tests. What is worth holding here is the contract this
/// app depends on: a real image for a real URL, and something renderable rather than an exception
/// for the states the screen has to draw before an invitation exists.
/// </remarks>
public class InviteQrCodeTests
{
    private const string InviteUrl = "https://api.example.test/connect?t=aXbYcZ0123456789aXbYcZ0123456789aXbYcZ01";

    /// <summary>The eight bytes every PNG begins with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void RendersAPng_ForAnInvitationUrl()
    {
        var png = InviteQrCode.Render(InviteUrl);

        Assert.NotNull(png);
        Assert.True(png.Length > PngSignature.Length);
        Assert.Equal(PngSignature, png.Take(PngSignature.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNull_WhenThereIsNothingToEncode(string? url)
    {
        // The waiting screen has to render before its invitation has arrived, so "no code yet" is a
        // state rather than a failure.
        Assert.Null(InviteQrCode.Render(url));
    }

    [Fact]
    public void ADifferentUrl_ProducesADifferentCode()
    {
        var first = InviteQrCode.Render(InviteUrl);
        var second = InviteQrCode.Render(InviteUrl.Replace("aXbY", "zZzZ"));

        // Guards the mistake that would be hardest to see on a screen: a cached or constant image
        // that looks like a QR code and sends every wearer to the same invitation.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheSameUrl_ProducesTheSameCode()
    {
        Assert.Equal(InviteQrCode.Render(InviteUrl), InviteQrCode.Render(InviteUrl));
    }
}
