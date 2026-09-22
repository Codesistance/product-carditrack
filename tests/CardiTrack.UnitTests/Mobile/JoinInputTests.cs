using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class JoinInputTests
{
    private const string Token = "kQ3bV9xLm2Zp8tRw5yHn7cDf4gJs6aBe1uYo0iEl";

    [Theory]
    [InlineData("KTR7-M2Q9")]
    [InlineData("ktr7 m2q9")]
    [InlineData("  KTR7M2Q9  ")]
    [InlineData("ktr7_m2q9")]
    public void ATypedFamilyIdNormalisesToTheStoredEightCharacters(string typed)
    {
        var parsed = JoinInput.Parse(typed);

        var code = Assert.IsType<JoinInput.FamilyCode>(parsed);
        Assert.Equal("KTR7M2Q9", code.FamilyId);
    }

    [Fact]
    public void AnInvitationLinkYieldsItsToken()
    {
        var parsed = JoinInput.Parse($"https://api.dev.carditrack.com/join?t={Token}");

        var invitation = Assert.IsType<JoinInput.Invitation>(parsed);
        Assert.Equal(Token, invitation.Token);
    }

    [Fact]
    public void ACustomSchemeLinkIsReadTheSameWay()
    {
        var parsed = JoinInput.Parse($"carditrack://join?t={Token}");

        Assert.IsType<JoinInput.Invitation>(parsed);
    }

    [Fact]
    public void ALinkCarryingAFamilyIdIsTreatedExactlyAsTypedEntry()
    {
        // D-11: the link is a convenience wrapper around the same identifier, not a second channel.
        var parsed = JoinInput.Parse("https://api.dev.carditrack.com/join?family=ktr7-m2q9");

        var code = Assert.IsType<JoinInput.FamilyCode>(parsed);
        Assert.Equal("KTR7M2Q9", code.FamilyId);
    }

    [Fact]
    public void ABareTokenIsAnInvitation()
    {
        var parsed = JoinInput.Parse(Token);

        Assert.IsType<JoinInput.Invitation>(parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("KTR7")]
    [InlineData("KTR7-M2Q0")]
    [InlineData("https://api.dev.carditrack.com/join")]
    [InlineData("https://api.dev.carditrack.com/join?t=short")]
    [InlineData("not a code at all")]
    public void AnythingElseIsNothing(string? text)
    {
        Assert.Null(JoinInput.Parse(text));
    }

    [Fact]
    public void TheHostOfALinkIsNotTrusted_OnlyItsQueryIsRead()
    {
        var parsed = JoinInput.Parse($"https://evil.example/anything?t={Token}");

        Assert.IsType<JoinInput.Invitation>(parsed);
    }
}
