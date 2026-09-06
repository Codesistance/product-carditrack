using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The answer to "does he need help with his sleep?" — the stored suggestion, served, and
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
        string? guideline = "Adult sleep duration (National Sleep Foundation)",
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
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        Assert.Contains("His sleep has been under seven hours most nights this week.", reply,
            StringComparison.Ordinal);
        Assert.Contains("A steadier bedtime could help him settle earlier.", reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The suggestion leads. The question this rung answers is "what could I do?", and a reply
    /// that opened with three sentences of readings before the one actionable sentence read as
    /// not answering it — a caregiver said so. The summary still travels, as grounding after.
    /// </summary>
    [Fact]
    public void ItLeadsWithTheSuggestion_NotTheSummary()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        Assert.StartsWith("A steadier bedtime could help him settle earlier.", reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// It closes by marking what it just said as a suggestion. A chat bubble has no heading and no
    /// layout to do that, where the Details card has both.
    /// </summary>
    [Fact]
    public void ItMarksWhatItSaidAsASuggestion()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        Assert.Contains("That's just an idea to consider", reply, StringComparison.Ordinal);
        Assert.Contains("their doctor is the one to ask", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// One doctor line, never two. The generation prompt asks the model for "worth mentioning to
    /// their doctor", so most stored rows already carry that route — and appending the framing
    /// line on top told a caregiver to see the doctor twice in three sentences (their words: only
    /// one line of the see-your-doctor message was needed).
    /// </summary>
    [Theory]
    [InlineData("It's just a thought, worth mentioning to his doctor.")]
    [InlineData("Something to run past his GP when convenient.")]
    [InlineData("Worth asking their physician about.")]
    [InlineData("His doctors would know if this suits him.")]
    [InlineData("One for his clinicians next visit.")]
    public void ARowThatAlreadyRoutesToTheDoctor_IsNotToldTwice(string doctorTail)
    {
        var reply = MemberChatReplies.AdviseReply(
            "Dad", Advise(suggestion: $"A short stroll could help. {doctorTail}"), Now);

        Assert.DoesNotContain("That's just an idea to consider", reply, StringComparison.Ordinal);
        Assert.Contains(doctorTail, reply, StringComparison.Ordinal);
    }

    /// <summary>The word alone is not the route — "doctorate" or "bedside" must not swallow the
    /// framing line. Whole words only.</summary>
    [Fact]
    public void ADoctorLookalikeWord_DoesNotSuppressTheFramingLine()
    {
        var reply = MemberChatReplies.AdviseReply(
            "Dad", Advise(suggestion: "His doctorate routine of evening reading could resume."), Now);

        Assert.Contains("That's just an idea to consider", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Never the word "wellness", on any path. It was in the tone block, twice more in the Advise
    /// prompt and again as a section heading, and MedGemma completes from the nearest text — so a
    /// suggestion came back saying "it's a general wellness thing" and this reply closed by saying
    /// it again four sentences later. The reply is also asserted on here, and not only the prompts,
    /// because this is the surface where a caregiver actually read it twice.
    /// </summary>
    [Fact]
    public void ItNeverSaysWellness()
    {
        foreach (var reply in new[]
                 {
                     MemberChatReplies.AdviseReply("Dad", Advise(), Now),
                     MemberChatReplies.AdviseReply("Dad", null, Now),
                 })
            Assert.DoesNotContain("wellness", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The authority is quoted at the end as a Reference line — the same convention inference
    /// closes with (decision 2026-08-24) — and never woven into the prose, which is what made an
    /// early version read like a leaflet. The quoted words are <see cref="WellnessGuidelines"/>'
    /// fixed lines, mapped from the stored pick, never the model's own text.
    /// </summary>
    [Fact]
    public void ItQuotesItsAuthority_AtTheEnd_NotInTheProse()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        Assert.EndsWith(
            "Reference: National Sleep Foundation — recommended nightly sleep 7–9 hours for adults, 7–8 hours from 65.",
            reply, StringComparison.Ordinal);
        Assert.DoesNotContain("based on", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stored pick the closed set does not carry quotes nothing — never an invented authority.
    /// The row still serves: the grounding gate ran at generation time; only the quote is
    /// withheld.
    /// </summary>
    [Fact]
    public void AnUnmappablePick_QuotesNothing()
    {
        var reply = MemberChatReplies.AdviseReply(
            "Dad", Advise(guideline: "General wellbeing literature"), Now);

        Assert.Contains("steadier bedtime", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Reference:", reply, StringComparison.Ordinal);
    }

    /// <summary>The honest empty case carries no Reference line either — there is nothing it
    /// would be the authority for.</summary>
    [Fact]
    public void TheEmptyCase_CarriesNoReferenceLine()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", null, Now);

        Assert.DoesNotContain("Reference:", reply, StringComparison.Ordinal);
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
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(guideline: guideline), Now);

        Assert.Contains("don't have a suggestion for Dad", reply, StringComparison.Ordinal);
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

        var reply = MemberChatReplies.AdviseReply("Dad", stale, Now);

        Assert.Contains("isn't a current one", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("steadier bedtime", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowInsideTheCeilingIsStillServed()
    {
        var borderline = Advise(age: AdviseStaleness.MaxAge - TimeSpan.FromHours(1));

        var reply = MemberChatReplies.AdviseReply("Dad", borderline, Now);

        Assert.Contains("steadier bedtime", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty case says why there is nothing and what can be asked instead. An advice question
    /// is one a caregiver asks when they are worried, which is the worst moment to answer only "no".
    /// </summary>
    [Fact]
    public void NoRowAtAll_SaysWhyAndWhatCanBeAskedInstead()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", null, Now);

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
        var reply = MemberChatReplies.AdviseReply(name, null, Now);

        Assert.Contains("suggestion for them right now", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Never a diagnosis and never an instruction, whatever the stored row says. The row is written
    /// under <c>ToneWellness</c>, but this is the surface where a caregiver reads it beside answers
    /// about their own family member, so the boundary is asserted where it is served too.
    /// </summary>
    [Fact]
    public void ItNeverReadsAsAClinicalInstruction()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        foreach (var claim in new[] { "you should", "you must", "diagnos", "prescri", "stop taking" })
            Assert.DoesNotContain(claim, reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "What kind of exercises can he do" was answered with "add more movement, like short walks
    /// during breaks". The caregiver asked WHICH; they were told DO MORE, with nothing marking the
    /// two as different questions.
    /// <para>
    /// The row itself stays exactly as it is — advise is never generated per question, and that is
    /// what earns it the only suggestion licence on this platform. What was missing was honesty
    /// about fit.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("what kind of exercises can he do")]
    [InlineData("how much walking is enough?")]
    [InlineData("how often should he be getting up?")]
    [InlineData("is it safe for him to walk that far?")]
    public void ASpecificsQuestion_IsToldTheSuggestionIsAStandingOne(string question)
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now, question);

        Assert.Contains("standing suggestion for Dad", reply, StringComparison.Ordinal);
        Assert.Contains("rather than an answer to exactly what you asked", reply, StringComparison.Ordinal);
        // The row is still served in full — this frames it, it does not withhold it.
        Assert.Contains("A steadier bedtime could help him settle earlier.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary advice question is one the standing suggestion genuinely answers, and saying
    /// otherwise would undercut a reply that is doing its job.
    /// </summary>
    [Theory]
    [InlineData("does he need help with his sleep?")]
    [InlineData("should I be worried about his walking?")]
    [InlineData("what can I do about how little he's walking?")]
    public void AnOrdinaryAdviceQuestion_IsNotHedged(string question)
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now, question);

        Assert.DoesNotContain("standing suggestion", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// One doctor mention, not two. The specifics framing already routes to a clinician, so the
    /// generic line must suppress itself the same way it does for a stored row that mentions one —
    /// telling a caregiver to see the doctor twice in three sentences is a nag.
    /// </summary>
    [Fact]
    public void TheSpecificsFramingDoesNotStackASecondDoctorLine()
    {
        var reply = MemberChatReplies.AdviseReply(
            "Dad", Advise(suggestion: "A short walk after lunch is worth trying."), Now,
            "what kind of exercises can he do");

        Assert.DoesNotContain("That's just an idea to consider", reply, StringComparison.Ordinal);
        var mentions = reply.Split("doctor", StringSplitOptions.None).Length - 1;
        Assert.True(mentions == 1, $"expected one doctor mention, found {mentions}: {reply}");
    }

    /// <summary>No question given — the older callers, and the framing simply does not apply.</summary>
    [Fact]
    public void WithNoQuestionAtAll_NothingIsAdded()
    {
        var reply = MemberChatReplies.AdviseReply("Dad", Advise(), Now);

        Assert.DoesNotContain("standing suggestion", reply, StringComparison.Ordinal);
    }
}
