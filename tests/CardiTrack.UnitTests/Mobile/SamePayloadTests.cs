using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The test every cache-first screen uses to decide it need not redraw. It exists because
/// hand-written comparators were getting this wrong in the one direction that matters: naming a
/// few fields, missing the rest, and calling a changed answer unchanged.
/// </summary>
public class SamePayloadTests
{
    private sealed record Rule(string Id, string Title, bool Enabled, bool IsImplemented);

    private sealed record Prefs(string Owner, IReadOnlyList<Rule> Rules);

    private static Prefs Sample() => new("dad", [new Rule("hr-high", "Heart rate is high", true, false)]);

    [Fact]
    public void Same_ForIdenticalPayloads()
    {
        Assert.True(SamePayload.Same(Sample(), Sample()));
    }

    /// <summary>
    /// The regression this replaced comparators for: a rule going from "Soon" to live changes
    /// nothing an id-and-on/off check looks at, and the screen would have kept the old badge.
    /// </summary>
    [Fact]
    public void Different_WhenAFieldTheScreenDrawsChanges_EvenThoughIdAndEnabledMatch()
    {
        var before = Sample();
        var after = before with { Rules = [before.Rules[0] with { IsImplemented = true }] };

        Assert.False(SamePayload.Same(before, after));
    }

    [Fact]
    public void Different_WhenCopyChanges()
    {
        var before = Sample();
        var after = before with { Rules = [before.Rules[0] with { Title = "Resting heart rate is high" }] };

        Assert.False(SamePayload.Same(before, after));
    }

    [Fact]
    public void Different_WhenAnItemIsAddedOrRemoved()
    {
        var before = Sample();
        var after = before with { Rules = [.. before.Rules, new Rule("sleep", "Sleep looks different", true, true)] };

        Assert.False(SamePayload.Same(before, after));
        Assert.False(SamePayload.Same(after, before));
    }

    [Fact]
    public void Same_ForTheSameReference()
    {
        var prefs = Sample();
        Assert.True(SamePayload.Same(prefs, prefs));
    }

    [Fact]
    public void Different_WhenOnlyOneSideIsNull()
    {
        Assert.False(SamePayload.Same(Sample(), null));
        Assert.False(SamePayload.Same<Prefs?>(null, Sample()));
    }

    [Fact]
    public void Same_WhenBothAreNull()
    {
        Assert.True(SamePayload.Same<Prefs?>(null, null));
    }

    /// <summary>Order is the server's and part of the answer — a reordered list is a redraw.</summary>
    [Fact]
    public void Different_WhenTheOrderChanges()
    {
        var a = new Prefs("dad", [new Rule("a", "A", true, true), new Rule("b", "B", true, true)]);
        var b = new Prefs("dad", [new Rule("b", "B", true, true), new Rule("a", "A", true, true)]);

        Assert.False(SamePayload.Same(a, b));
    }
}
