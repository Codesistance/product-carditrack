using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Devices;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The words on the M1-15 re-pull row. Pinned because the card decides whether to offer the
/// action from the same facts it describes, and the two must not drift apart.
/// </summary>
public class HistoryRepullCopyTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Choices_AreTheSevenPresets_InOrder()
    {
        Assert.Equal([7, 14, 30, 45, 60, 75, 90], HistoryRepullCopy.DayChoices);
        Assert.Equal("Last 30 days", HistoryRepullCopy.ChoiceLabels()[2]);
    }

    [Theory]
    [InlineData("Last 7 days", 7)]
    [InlineData("Last 90 days", 90)]
    public void DaysFor_ReadsThePresetBackOutOfItsLabel(string label, int days)
    {
        Assert.Equal(days, HistoryRepullCopy.DaysFor(label));
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData(null)]
    [InlineData("Last 8 days")]
    public void DaysFor_IsNullForAnythingElse(string? label)
    {
        Assert.Null(HistoryRepullCopy.DaysFor(label));
    }

    [Theory]
    [InlineData(7, "about 10 minutes")]
    [InlineData(30, "about 50 minutes")]
    [InlineData(45, "about an hour")]
    [InlineData(90, "about 2 hours")]
    public void Estimate_ReadsAsAWait_NotAChunkCount(int days, string expected)
    {
        Assert.Equal(expected, HistoryRepullCopy.Estimate(days));
    }

    [Fact]
    public void StatusLine_IsNull_WhenThereIsNoRequest()
    {
        Assert.Null(HistoryRepullCopy.StatusLine(null, Now));
        Assert.True(HistoryRepullCopy.CanRequest(null, Now));
    }

    [Fact]
    public void StatusLine_ReportsProgress_AndWithholdsTheAction_WhileOpen()
    {
        var pending = new DeviceHistoryRepullResponse { Status = "pending", Days = 30 };
        Assert.Equal("Queued — starts within 10 minutes", HistoryRepullCopy.StatusLine(pending, Now));
        Assert.False(HistoryRepullCopy.CanRequest(pending, Now));

        var running = new DeviceHistoryRepullResponse { Status = "in_progress", Days = 30, DaysDone = 14 };
        Assert.Equal("Re-pulling last 30 days · 14 of 30 days done", HistoryRepullCopy.StatusLine(running, Now));
        Assert.False(HistoryRepullCopy.CanRequest(running, Now));
    }

    [Fact]
    public void StatusLine_ForACompletedRequestInsideTheCooldown_SaysWhenItIsAvailableAgain()
    {
        var done = new DeviceHistoryRepullResponse
        {
            Status = "completed", Days = 30, DaysDone = 30, DaysWithData = 27,
            NextAllowedAt = Now.AddHours(36),
        };

        Assert.Equal(
            "Done — 27 of 30 days had data · available again in about 36 hours",
            HistoryRepullCopy.StatusLine(done, Now));
        Assert.False(HistoryRepullCopy.CanRequest(done, Now));
    }

    /// <summary>
    /// The hours are rounded up, so the bound that ends them has to include 48 — otherwise a
    /// wait of 47 hours and a bit becomes 48 and falls into the days branch, sending the
    /// freshest cooldown a 48-hour setting can produce to the vaguest wording it has.
    /// </summary>
    [Theory]
    [InlineData(47.1, "available again in about 48 hours")]
    [InlineData(47.9, "available again in about 48 hours")]
    [InlineData(36, "available again in about 36 hours")]
    [InlineData(0.5, "available again in about an hour")]
    public void StatusLine_KeepsHoursRightUpToTheCooldownsCeiling(double hoursAway, string expected)
    {
        var done = new DeviceHistoryRepullResponse
        {
            Status = "completed", Days = 30, DaysDone = 30, DaysWithData = 27,
            NextAllowedAt = Now.AddHours(hoursAway),
        };

        Assert.EndsWith(expected, HistoryRepullCopy.StatusLine(done, Now));
    }

    [Fact]
    public void StatusLine_ForACompletedRequestPastTheCooldown_OffersTheActionAgain()
    {
        var done = new DeviceHistoryRepullResponse
        {
            Status = "completed", Days = 30, DaysDone = 30, DaysWithData = 27,
        };

        Assert.Equal("Done — 27 of 30 days had data", HistoryRepullCopy.StatusLine(done, Now));
        Assert.True(HistoryRepullCopy.CanRequest(done, Now));
    }

    [Theory]
    [InlineData("failed", "Didn't finish — you can try again")]
    [InlineData("cancelled", "Stopped — monitoring paused or the device changed")]
    public void StatusLine_ForATerminalFailure_OffersTheActionAgain_WithoutTheReason(string status, string expected)
    {
        var repull = new DeviceHistoryRepullResponse { Status = status, Days = 30 };

        Assert.Equal(expected, HistoryRepullCopy.StatusLine(repull, Now));
        Assert.True(HistoryRepullCopy.CanRequest(repull, Now));
    }
}
