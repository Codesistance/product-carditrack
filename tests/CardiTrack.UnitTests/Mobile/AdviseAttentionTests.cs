using CardiTrack.Mobile.Core.Insights;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The seen-tracking behind the Dashboard card's sparkle pulse: animate for a suggestion this
/// device has not shown yet, fall silent once the Quick actions card has shown it, and light up
/// again only when a genuinely newer one is generated.
/// </summary>
public class AdviseAttentionTests
{
    private static readonly DateTimeOffset Generated =
        new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANeverSeenSuggestionIsUnseen()
    {
        Assert.True(AdviseAttention.IsUnseen(Generated, null));
        Assert.True(AdviseAttention.IsUnseen(Generated, ""));
    }

    [Fact]
    public void TheSuggestionJustShownIsSeen()
    {
        Assert.False(AdviseAttention.IsUnseen(Generated, AdviseAttention.Stamp(Generated)));
    }

    [Fact]
    public void ANewerGenerationIsUnseenAgain()
    {
        var seenEarlier = AdviseAttention.Stamp(Generated.AddHours(-6));

        Assert.True(AdviseAttention.IsUnseen(Generated, seenEarlier));
    }

    /// <summary>
    /// A server that predates the stamp sends null while the button is visible. That reads as
    /// unseen — the pulse keeps its old always-on behaviour rather than falling silent on data
    /// it does not have.
    /// </summary>
    [Fact]
    public void NoServerStampReadsAsUnseen()
    {
        Assert.True(AdviseAttention.IsUnseen(null, AdviseAttention.Stamp(Generated)));
    }

    [Fact]
    public void AnUnreadableRecordReadsAsUnseen()
    {
        Assert.True(AdviseAttention.IsUnseen(Generated, "not-a-timestamp"));
    }

    /// <summary>The comparison is on the instant, not the wall clock it was written in.</summary>
    [Fact]
    public void ComparesInstantsAcrossOffsets()
    {
        var sameInstantElsewhere = Generated.ToOffset(TimeSpan.FromHours(3));

        Assert.False(AdviseAttention.IsUnseen(sameInstantElsewhere, AdviseAttention.Stamp(Generated)));
    }
}
