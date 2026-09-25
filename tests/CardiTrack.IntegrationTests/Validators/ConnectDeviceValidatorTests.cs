using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.IntegrationTests.Validators;

public class ConnectDeviceValidatorTests
{
    private readonly ConnectDeviceValidator _sut = new();

    private static ConnectDeviceRequest Request(string redirectUri) =>
        new() { Provider = "fitbit", RedirectUri = redirectUri };

    [Fact]
    public void Accepts_TheAppDeepLink()
    {
        Assert.True(_sut.Validate(Request("carditrack://oauth/callback")).IsValid);
    }

    // Brands, not APIs: pixel_watch is a distinct wire name even though it shares the
    // GoogleHealth engine with fitbit.
    [Theory]
    [InlineData("fitbit")]
    [InlineData("pixel_watch")]
    [InlineData("garmin")]
    public void Accepts_EveryServerOAuthBrand(string provider)
    {
        var request = new ConnectDeviceRequest { Provider = provider, RedirectUri = "carditrack://oauth/callback" };

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Rejects_AnUnknownProvider()
    {
        var request = new ConnectDeviceRequest { Provider = "apple_health", RedirectUri = "carditrack://oauth/callback" };

        var result = _sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ConnectDeviceRequest.Provider));
    }

    [Theory]
    [InlineData("carditrack://oauth/callback#done")]
    [InlineData("carditrack://oauth/callback?x=1#done")]
    public void Rejects_ARedirectCarryingAFragment(string redirectUri)
    {
        // The bounce endpoint appends state/code/error to this URI; anything after a '#'
        // would swallow them, so the app would come back with nothing to act on.
        var result = _sut.Validate(Request(redirectUri));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ConnectDeviceRequest.RedirectUri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("oauth/callback")]
    // A bare path is the case that makes an "absolute URI" check insufficient: on Linux —
    // where the API actually runs — Uri.TryCreate reads it as an absolute file: URI and
    // accepts it, so only the scheme check rejects it on both platforms.
    [InlineData("/oauth/callback")]
    [InlineData("https://attacker.example.com/collect")]
    public void Rejects_AnythingThatIsNotAnAppDeepLink(string redirectUri)
    {
        Assert.False(_sut.Validate(Request(redirectUri)).IsValid);
    }

    // ── Mode (issue #1286) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("add")]
    [InlineData("ADD")]
    public void Accepts_AnAdd_WithoutADeviceId(string? mode)
    {
        var request = Request("carditrack://oauth/callback");
        request.Mode = mode;

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("replace")]
    public void Accepts_AReconnectOrReplace_NamingItsDevice(string mode)
    {
        var request = Request("carditrack://oauth/callback");
        request.Mode = mode;
        request.DeviceId = Guid.NewGuid();

        Assert.True(_sut.Validate(request).IsValid);
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("replace")]
    public void Rejects_AReconnectOrReplace_WithoutADeviceId(string mode)
    {
        var request = Request("carditrack://oauth/callback");
        request.Mode = mode;

        var result = _sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ConnectDeviceRequest.DeviceId));
    }

    [Fact]
    public void Rejects_AnAdd_ThatNamesADevice()
    {
        // A client sending a device id with an add believes it is doing something else.
        var request = Request("carditrack://oauth/callback");
        request.DeviceId = Guid.NewGuid();

        Assert.False(_sut.Validate(request).IsValid);
    }

    [Fact]
    public void Rejects_AnUnknownMode()
    {
        var request = Request("carditrack://oauth/callback");
        request.Mode = "swap";

        var result = _sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ConnectDeviceRequest.Mode));
    }
}
