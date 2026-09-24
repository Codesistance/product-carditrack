using CardiTrack.Mobile.Core.Diagnostics;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// A stored telemetry choice belongs to the caregiver who made it: an expired session must not
/// hand one caregiver's "off" to the next person who signs in on the phone.
/// </summary>
public class TelemetryChoiceOwnerTests
{
    private static readonly string AdaOwner = TelemetryNotice.SeenValueFor("ada@example.com")!;

    [Fact]
    public void NoStoredChoice_Keeps()
    {
        Assert.Equal(TelemetryChoiceAction.Keep, TelemetryChoiceOwner.OnSignIn(false, null, "ada@example.com"));
    }

    [Fact]
    public void SameCaregiver_Keeps()
    {
        Assert.Equal(TelemetryChoiceAction.Keep, TelemetryChoiceOwner.OnSignIn(true, AdaOwner, "ADA@example.com "));
    }

    [Fact]
    public void DifferentCaregiver_Forgets()
    {
        Assert.Equal(TelemetryChoiceAction.Forget, TelemetryChoiceOwner.OnSignIn(true, AdaOwner, "grace@example.com"));
    }

    [Fact]
    public void ChoiceSavedBeforeOwnersWereRecorded_IsAdopted()
    {
        Assert.Equal(TelemetryChoiceAction.Adopt, TelemetryChoiceOwner.OnSignIn(true, null, "ada@example.com"));
        Assert.Equal(TelemetryChoiceAction.Adopt, TelemetryChoiceOwner.OnSignIn(true, string.Empty, "ada@example.com"));
    }

    [Fact]
    public void ChoiceMadeWithoutAnIdentity_IsForgottenNotAdopted_ByTheNextCaregiver()
    {
        var owner = TelemetryChoiceOwner.OwnerFor(null);

        Assert.Equal(TelemetryChoiceOwner.Unidentified, owner);
        Assert.Equal(TelemetryChoiceAction.Forget, TelemetryChoiceOwner.OnSignIn(true, owner, "grace@example.com"));
    }

    [Fact]
    public void OwnerFor_IsTheCaregiversToken()
    {
        Assert.Equal(AdaOwner, TelemetryChoiceOwner.OwnerFor("ada@example.com"));
    }

    [Fact]
    public void NoIdentity_KeepsRatherThanDropAnObjection()
    {
        Assert.Equal(TelemetryChoiceAction.Keep, TelemetryChoiceOwner.OnSignIn(true, AdaOwner, null));
    }
}
