using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The degenerate-generation guard. A looping model repeats the same two or three sentences over
/// and over — each one individually true, so every content check passes — and the result reached
/// a caregiver's screen as a summary that visibly was not a person writing (the same broken
/// decode also titled that card the opposite of what its own body said). One verbatim repeat of
/// a whole sentence is the tell.
/// </summary>
public class MedicalPromptRepetitionTests
{
    /// <summary>The shape that shipped: three sentences, said three times, word for word.</summary>
    [Fact]
    public void CatchesALoopedGeneration()
    {
        const string once =
            "His heart rate was higher than usual today, reaching 86 bpm. "
            + "His steps were lower than usual, at 5523 steps, and active minutes were also lower.";

        Assert.NotNull(MedicalPromptBlocks.RepeatedSentence($"{once} {once} {once}"));
    }

    [Fact]
    public void PassesAnOrdinarySummary()
    {
        Assert.Null(MedicalPromptBlocks.RepeatedSentence(
            "They had a quieter day than usual, with fewer steps by this hour. "
            + "Their heart rate stayed in its normal range, and last night's sleep was close to "
            + "their usual. Worth a call this afternoon to see how they're feeling."));
    }

    /// <summary>Re-punctuation must not sneak a loop through: the wording is what repeats.</summary>
    [Fact]
    public void ComparesWordingNotPunctuation()
    {
        Assert.NotNull(MedicalPromptBlocks.RepeatedSentence(
            "Their steps were lower than usual today. Their steps were lower than usual, today!"));
    }

    /// <summary>
    /// Short clauses recur honestly in prose — only a whole sentence of four words or more is
    /// treated as the loop.
    /// </summary>
    [Fact]
    public void IgnoresShortFragments()
    {
        Assert.Null(MedicalPromptBlocks.RepeatedSentence(
            "All steady. They walked their usual morning route and slept well. All steady."));
    }

    [Fact]
    public void ReturnsTheSentenceThatRepeated()
    {
        var repeated = MedicalPromptBlocks.RepeatedSentence(
            "He rested more than usual today. His heart rate stayed steady through the morning. "
            + "He rested more than usual today.");

        Assert.Equal("He rested more than usual today", repeated);
    }
}
