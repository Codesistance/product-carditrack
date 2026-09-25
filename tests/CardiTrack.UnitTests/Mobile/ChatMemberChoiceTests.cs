using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

public class ChatMemberChoiceTests
{
    [Fact]
    public void Paused_SaysSo_WhateverTheTier()
    {
        Assert.Equal(("Monitoring paused", "StatusUnknown"),
            ChatMemberChoice.Status("green", paused: true, everSynced: true, savedHeadline: "All steady"));
        Assert.Equal(("Monitoring paused", "StatusUnknown"),
            ChatMemberChoice.Status("paused", paused: false, everSynced: true, savedHeadline: null));
    }

    [Fact]
    public void NeverSynced_SaysNoReadingsYet_NotATier()
    {
        // A tier the server defaulted before any reading arrived must not read as a verdict.
        Assert.Equal((ChatMemberChoice.NoReadingsYet, "StatusUnknown"),
            ChatMemberChoice.Status("green", paused: false, everSynced: false, savedHeadline: null));
    }

    [Fact]
    public void SavedHeadline_WinsOverTheTierCopy_InTheTiersColour()
    {
        Assert.Equal(("Slightly lower activity and sleep", "StatusYellow"),
            ChatMemberChoice.Status("yellow", false, true, "  Slightly lower activity and sleep "));
    }

    [Theory]
    [InlineData("green", "All steady", "StatusGreen")]
    [InlineData("yellow", "Something's different", "StatusYellow")]
    [InlineData("orange", "Worth a check-in", "StatusOrange")]
    [InlineData("red", "Reach out now", "StatusRed")]
    public void WithoutASavedLine_UsesTheHerosHeadlineForTheTier(string tier, string text, string colour)
    {
        Assert.Equal((text, colour), ChatMemberChoice.Status(tier, false, true, null));
    }

    [Fact]
    public void UnknownTier_WithReadings_HasNoLine()
    {
        Assert.Equal(((string?)null, "StatusUnknown"), ChatMemberChoice.Status("unknown", false, true, ""));
    }
}
