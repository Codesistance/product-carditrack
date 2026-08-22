using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The answer to "does he need help with his sleep?" — the stored wellness suggestion, served, and
/// never assembled here. Asked that question the chat used to reach the planner, which knows only
/// the four <c>DataQueryKind</c> sources, and came back with a readback of the week and a sleep
/// chart: every figure correct, and no answer to what was asked.
/// </summary>
public class AdviseReplyTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 7, 12, 0, DateTimeKind.Utc);

    private static MemberAdvise Advise(
        string summary = "His sleep has been under seven hours most nights this week.",
        string suggestion = "A steadier bedtime could help him settle earlier.",
        string? guideline = "Adult sleep duration (AASM/CDC)",
        TimeSpan? age = null) => new()
        {
            Summary = summary,
            Suggestion = suggestion,
            GuidelineCited = guideline,
            GeneratedAtUtc = Now - (age ?? TimeSpan.FromHours(6)),
        };

    [Fact]
    public void ItServesTheStoredSummaryAndSuggestion()
    {
        var reply = MemberChatService.AdviseReply("Dad", Advise(), Now);

        Assert.Contains("His sleep has been under seven hours most nights this week.", reply,
            StringComparison.Ordinal);
        Assert.Contains("A steadier bedtime could help him settle earlier.", reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The grounding travels with the suggestion. The Details card can put it in muted small type
    /// under the suggestion; a chat bubble has no small type to put it in.
    /// </summary>
    [Fact]
    public void ItNamesWhatTheSuggestionRestsOnAndWhatItIsNot()
    {
        var reply = MemberChatService.AdviseReply("Dad", Advise(), Now);

        Assert.Contains("general wellness guidance based on Adult sleep duration (AASM/CDC)", reply,
            StringComparison.Ordinal);
        Assert.Contains("worth mentioning to their doctor rather than acting on by itself", reply,
            StringComparison.Ordinal);
    }

    /// <summary>A citation that came back with a full stop must not stop the closing clause dead.</summary>
    [Fact]
    public void ACitationsOwnFullStopIsNotLeftMidSentence()
    {
        var reply = MemberChatService.AdviseReply(
            "Dad", Advise(guideline: "Adult physical activity (WHO, 2020)."), Now);

        Assert.Contains("(WHO, 2020) — worth mentioning", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("2020). —", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A suggestion with nothing behind it is not served bare — the same call
    /// <c>AdviseGenerationService</c> makes when it withholds the row rather than persisting it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUngroundedSuggestionIsWithheld(string? guideline)
    {
        var reply = MemberChatService.AdviseReply("Dad", Advise(guideline: guideline), Now);

        Assert.Contains("don't have a wellness suggestion for Dad", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("steadier bedtime", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Past the staleness ceiling the row is treated as though generation has stopped — the same
    /// line <c>HealthInsightService.GetAdviseAsync</c> and <c>DashboardService</c> draw, so the
    /// three surfaces cannot disagree about whether there is a current suggestion.
    /// </summary>
    [Fact]
    public void AStaleRowIsNotServed()
    {
        var stale = Advise(age: AdviseStaleness.MaxAge + TimeSpan.FromHours(1));

        var reply = MemberChatService.AdviseReply("Dad", stale, Now);

        Assert.Contains("isn't a current one", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("steadier bedtime", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowInsideTheCeilingIsStillServed()
    {
        var borderline = Advise(age: AdviseStaleness.MaxAge - TimeSpan.FromHours(1));

        var reply = MemberChatService.AdviseReply("Dad", borderline, Now);

        Assert.Contains("steadier bedtime", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty case says why there is nothing and what can be asked instead. An advice question
    /// is one a caregiver asks when they are worried, which is the worst moment to answer only "no".
    /// </summary>
    [Fact]
    public void NoRowAtAll_SaysWhyAndWhatCanBeAskedInstead()
    {
        var reply = MemberChatService.AdviseReply("Dad", null, Now);

        Assert.Contains("once a day", reply, StringComparison.Ordinal);
        Assert.Contains("compare with what's usual for them", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member with no name on file still gets a sentence that reads — the failure
    /// <c>LiveStatusReply</c>'s own unnamed case exists for.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingNameStillReadsAsEnglish(string? name)
    {
        var reply = MemberChatService.AdviseReply(name, null, Now);

        Assert.Contains("wellness suggestion for them right now", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Never a diagnosis and never an instruction, whatever the stored row says. The row is written
    /// under <c>ToneWellness</c>, but this is the surface where a caregiver reads it beside answers
    /// about their own family member, so the boundary is asserted where it is served too.
    /// </summary>
    [Fact]
    public void ItNeverReadsAsAClinicalInstruction()
    {
        var reply = MemberChatService.AdviseReply("Dad", Advise(), Now);

        foreach (var claim in new[] { "you should", "you must", "diagnos", "prescri", "stop taking" })
            Assert.DoesNotContain(claim, reply, StringComparison.OrdinalIgnoreCase);
    }
}
