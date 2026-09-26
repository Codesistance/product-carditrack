using CardiTrack.Mobile.Core.Chat;

namespace CardiTrack.UnitTests.Mobile;

public class ChatFunFactsTests
{
    [Fact]
    public void EveryFact_IsDifferent_AndNamesTheMember()
    {
        var facts = Enumerable.Range(0, ChatFunFacts.Count).Select(i => ChatFunFacts.At(i, "Pop")).ToList();

        Assert.Equal(facts.Count, facts.Distinct().Count());
        Assert.All(facts.Where(f => !f.StartsWith("Some of my answers", StringComparison.Ordinal)),
            f => Assert.Contains("Pop", f));
    }

    [Fact]
    public void TheIndex_WrapsRound_InBothDirections()
    {
        Assert.Equal(ChatFunFacts.At(0, "Pop"), ChatFunFacts.At(ChatFunFacts.Count, "Pop"));
        Assert.Equal(ChatFunFacts.At(ChatFunFacts.Count - 1, "Pop"), ChatFunFacts.At(-1, "Pop"));
    }

    [Fact]
    public void WithNobodyChosen_TheFactsSpeakOfThem()
    {
        var facts = Enumerable.Range(0, ChatFunFacts.Count).Select(i => ChatFunFacts.At(i, "  ")).ToList();

        Assert.DoesNotContain(facts, f => f.Contains("  ", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Contains("their", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameEndingInS_TakesABareApostrophe()
    {
        Assert.Contains("James' heart rate", ChatFunFacts.At(0, "James"));
        Assert.Contains("Pop's heart rate", ChatFunFacts.At(0, "Pop"));
    }
}
