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
    [InlineData("/oauth/callback")]
    public void Rejects_AnythingThatIsNotAnAbsoluteUri(string redirectUri)
    {
        Assert.False(_sut.Validate(Request(redirectUri)).IsValid);
    }
}
