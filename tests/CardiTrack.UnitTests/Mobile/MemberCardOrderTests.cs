using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

public class MemberCardOrderTests
{
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Cal = Guid.NewGuid();
    private static readonly Guid Dee = Guid.NewGuid();

    [Fact]
    public void Orders_TheMostWorryingFirst_ThenByName()
    {
        var order = MemberCardOrder.Order(
        [
            (Ann, "green", "Ann"),
            (Bob, "orange", "Bob"),
            (Cal, "unknown", "Cal"),
            (Dee, "orange", "Aggie"),
        ], new HashSet<Guid>());

        // Orange ones first (by name), then the one we cannot read yet, then the one who is fine.
        Assert.Equal([Dee, Bob, Cal, Ann], order);
    }

    [Fact]
    public void Pinned_ComeFirst_WhateverTheirStatus_AndKeepStatusOrderAmongThemselves()
    {
        var order = MemberCardOrder.Order(
        [
            (Ann, "green", "Ann"),
            (Bob, "red", "Bob"),
            (Cal, "paused", "Cal"),
            (Dee, "yellow", "Dee"),
        ], new HashSet<Guid> { Ann, Dee });

        Assert.Equal([Dee, Ann, Bob, Cal], order);
    }

    [Theory]
    [InlineData("red", 0)]
    [InlineData("orange", 1)]
    [InlineData("yellow", 2)]
    [InlineData("unknown", 3)]
    [InlineData("something-new", 3)]
    [InlineData(null, 3)]
    [InlineData("green", 4)]
    [InlineData("paused", 5)]
    public void StatusRank_PutsAPausedMemberLast_AndAnUnreadableOneBeforeAHealthyOne(string? status, int rank)
    {
        Assert.Equal(rank, MemberCardOrder.StatusRank(status));
    }

    [Fact]
    public void Pins_RoundTrip_AndIgnoreJunk()
    {
        var stored = MemberCardOrder.FormatPins([Ann, Bob]);

        Assert.Equal(new HashSet<Guid> { Ann, Bob }, MemberCardOrder.ParsePins(stored));
        Assert.Equal(new HashSet<Guid> { Ann }, MemberCardOrder.ParsePins($"{Ann:N}, not-a-guid,,"));
        Assert.Empty(MemberCardOrder.ParsePins(null));
    }
}
