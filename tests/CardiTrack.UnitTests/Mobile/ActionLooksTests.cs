using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

public class ActionLooksTests
{
    [Theory]
    [InlineData("Save", ActionTone.Blue, "icon_btn_save.svg")]
    [InlineData("Connect a device", ActionTone.Blue, "icon_btn_connect.svg")]
    [InlineData("Yes", ActionTone.Green, "icon_check_white.svg")]
    [InlineData("No", ActionTone.Dark, "icon_btn_close.svg")]
    [InlineData("No thanks", ActionTone.Dark, "icon_btn_close.svg")]
    [InlineData("Remove this alert", ActionTone.Red, "icon_btn_delete.svg")]
    [InlineData("Delete", ActionTone.Red, "icon_btn_delete.svg")]
    [InlineData("Undo", ActionTone.Amber, "icon_btn_undo.svg")]
    [InlineData("Cancel", ActionTone.Dark, "icon_btn_close.svg")]
    [InlineData("Sign out", ActionTone.Red, null)]
    public void TheWordsOnAButtonDecideItsLook(string label, ActionTone tone, string? icon)
    {
        Assert.Equal(new ActionLook(tone, icon), ActionLooks.For(label));
    }

    [Fact]
    public void AnUnrecognisedStepForwardIsBlueWithoutAnIcon()
    {
        Assert.Equal(new ActionLook(ActionTone.Blue, null), ActionLooks.For("Send invite"));
    }

    [Theory]
    [InlineData("Cancel", ActionTone.Dark, "icon_btn_close.svg")]
    [InlineData("Not now", ActionTone.Dark, "icon_btn_later.svg")]
    [InlineData("Keep it", ActionTone.Dark, "icon_btn_close.svg")]
    [InlineData("No", ActionTone.Dark, "icon_btn_close.svg")]
    public void TheWayOutOfADialogIsDarkWhateverItSays(string label, ActionTone tone, string icon)
    {
        Assert.Equal(new ActionLook(tone, icon), ActionLooks.For(label, isDismiss: true));
    }

    [Theory]
    [InlineData("Yes, remove", ActionTone.Red, "icon_btn_delete.svg")]
    [InlineData("Yes, delete", ActionTone.Red, "icon_btn_delete.svg")]
    [InlineData("Yes, continue", ActionTone.Blue, null)]
    [InlineData("Yes, skip", ActionTone.Blue, null)]
    public void AYesThatNamesItsDeedIsDressedAsTheDeed(string label, ActionTone tone, string? icon)
    {
        Assert.Equal(new ActionLook(tone, icon), ActionLooks.For(label));
    }

    [Theory]
    [InlineData("Cancel it", ActionTone.Red, "icon_btn_close.svg")]
    [InlineData("Cancel", ActionTone.Dark, "icon_btn_close.svg")]
    public void CancellingSomethingEndsItWhereABareCancelBacksOut(string label, ActionTone tone, string icon)
    {
        Assert.Equal(new ActionLook(tone, icon), ActionLooks.For(label));
    }
}
