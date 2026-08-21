using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The answer to "is he asleep now?" — assembled in code precisely so no model can assemble a
/// different one. Asked that question the clinical read replied "Yes, Dad is asleep now",
/// inferred from a nightly sleep total, and a prompt rule forbidding it did not hold. This
/// platform has no live signal of any kind: sleep is a nightly total attributed to the morning it
/// ended, steps are a running daily count, and the closest thing to real time is an hourly
/// heart-rate assessment.
/// </summary>
public class LiveStatusReplyTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);

    private static ActivityLog Log(DateOnly date, int? steps = null, int? hr = null, int? sleep = null) =>
        new() { Date = date, Steps = steps, RestingHeartRate = hr, SleepMinutes = sleep };

    /// <summary>
    /// The refusal comes first and is unconditional. A reading offered ahead of it would be read
    /// as the answer to the question that was asked, which is the failure this replaces.
    /// </summary>
    [Fact]
    public void ItSaysWhatItCannotSee_BeforeAnythingItCan()
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [Log(Today, steps: 4200)], Today);

        Assert.StartsWith("I can't see what Dad is doing right now", reply, StringComparison.Ordinal);
        Assert.Contains("nothing live here to check", reply, StringComparison.Ordinal);
        Assert.True(
            reply.IndexOf("can't see", StringComparison.Ordinal) < reply.IndexOf("4,200", StringComparison.Ordinal),
            "the limit has to be stated before the figure, or the figure reads as the answer.");
    }

    /// <summary>
    /// Never a present-tense claim about the person, whatever the readings say. A night of sleep
    /// on today's row is last night's, not now.
    /// </summary>
    [Theory]
    [InlineData(480)]
    [InlineData(0)]
    public void ItNeverSaysTheyAreAsleepOrAwake(int sleepMinutes)
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [Log(Today, sleep: sleepMinutes)], Today);

        foreach (var claim in new[] { "is asleep", "is awake", "is sleeping", "is resting", "is up" })
            Assert.DoesNotContain(claim, reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every figure is dated. "4,200 steps" with no day attached invites exactly the present-tense
    /// reading this path exists to prevent.
    /// </summary>
    [Fact]
    public void TodaysFiguresSayTodaySoFar()
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [Log(Today, steps: 4200, hr: 62)], Today);

        Assert.Contains("today so far", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4,200 steps", reply, StringComparison.Ordinal);
        Assert.Contains("62 bpm", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderReadingIsNamedByItsDay()
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [Log(Today.AddDays(-1), steps: 3100)], Today);

        Assert.Contains("yesterday", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The most recent row wins, whatever order the repository returned them in.</summary>
    [Fact]
    public void TheLatestRowIsTheOneQuoted()
    {
        var reply = MemberChatService.LiveStatusReply(
            "Dad",
            [Log(Today, steps: 900), Log(Today.AddDays(-2), steps: 8888), Log(Today.AddDays(-1), steps: 7777)],
            Today);

        Assert.Contains("900 steps", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("8,888", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("7,777", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row the wearable never filled in is not a reading. Quoting an empty row as "the most
    /// recent I have" would be the same false confidence in a quieter form.
    /// </summary>
    [Fact]
    public void RowsWithNoReadingsAreSkipped()
    {
        var reply = MemberChatService.LiveStatusReply(
            "Dad", [Log(Today.AddDays(-1), steps: 5000), Log(Today)], Today);

        Assert.Contains("yesterday", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5,000 steps", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReadingsAtAll_SaysSoRatherThanTrailingOff()
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [], Today);

        Assert.Contains("don't have any recent readings", reply, StringComparison.Ordinal);
    }

    /// <summary>Sleep reads in hours here as everywhere else — see <c>SleepFigureTests</c>.</summary>
    [Fact]
    public void SleepIsSpokenInHours()
    {
        var reply = MemberChatService.LiveStatusReply("Dad", [Log(Today, sleep: 372)], Today);

        Assert.Contains("6h 12m", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("372", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member with no name still gets a sentence that reads. Substituting a noun into the named
    /// form produces "what them is doing", which this caught on the first run — so the unnamed
    /// case takes its own subject rather than a word swapped into the same slot.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingNameStillReadsAsEnglish(string? name)
    {
        var reply = MemberChatService.LiveStatusReply(name, [Log(Today, steps: 100)], Today);

        Assert.StartsWith("I can't see what they're doing right now", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralReadingsAreJoinedAsASentence()
    {
        var reply = MemberChatService.LiveStatusReply(
            "Dad", [Log(Today, steps: 4200, hr: 62, sleep: 400)], Today);

        Assert.Contains("4,200 steps, a resting heart rate of 62 bpm and 6h 40m of sleep", reply,
            StringComparison.Ordinal);
    }
}
