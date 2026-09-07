using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// What the app's own tick box tells a screen reader. The order matters: a row whose label is
/// links and a sentence rather than plain words — the sign-up form's terms line — has no Text to
/// fall back on, and must not be announced as an unnamed box when the page gave it a description.
/// </summary>
public class CheckDescriptionTests
{
    [Fact]
    public void ThePagesDescription_Wins()
    {
        Assert.Equal(
            "I agree to the Terms of Service and Privacy Policy, not ticked",
            CheckDescription.For("I agree to the Terms of Service and Privacy Policy", "I agree to the", isChecked: false));
    }

    [Fact]
    public void WithoutOne_TheLabelIsTheName()
    {
        Assert.Equal("Remember me, ticked", CheckDescription.For(null, "Remember me", isChecked: true));
    }

    [Fact]
    public void ABlankDescription_CountsAsNone()
    {
        Assert.Equal("Alerts and events", CheckDescription.Name("   ", "Alerts and events"));
    }

    [Fact]
    public void WithNeither_TheBoxIsCalledWhatItIs()
    {
        Assert.Equal("Tick box, not ticked", CheckDescription.For(null, null, isChecked: false));
        Assert.Equal(CheckDescription.Unnamed, CheckDescription.Name(string.Empty, string.Empty));
    }

    [Theory]
    [InlineData(true, "ticked")]
    [InlineData(false, "not ticked")]
    public void TheStateFollowsTheName(bool isChecked, string state)
    {
        Assert.EndsWith(", " + state, CheckDescription.For(null, "Remember me", isChecked));
    }
}
