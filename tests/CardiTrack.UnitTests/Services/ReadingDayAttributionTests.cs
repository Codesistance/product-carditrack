using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The day a figure belongs to, on the rungs that generate their prose.
/// </summary>
/// <remarks>
/// An observed session asked "How are they doing today?" and then "anything to followup on?" in
/// the same minute, and got 4,905 steps and then 4,475 — figures that cannot both be one day,
/// because steps only ever climb within a day. Neither reply said which day it meant. The status
/// rung had dated its figures in code since it was written; the generated rungs left the day to
/// the rewrite model, which is briefed on tone and register and not on dates, and which turned
/// "Yesterday (complete day)" into "a stable day".
/// <para>
/// So the model names <em>which</em> dates and this code writes <em>what</em> a caregiver reads —
/// the same split the References line and the Advise citation already work by.
/// </para>
/// </remarks>
public class ReadingDayAttributionTests
{
    private static readonly DateOnly Today = new(2026, 9, 4);
    private static readonly (DateOnly From, DateOnly To) Window = (new(2026, 8, 29), Today);

    // ── The shared vocabulary ───────────────────────────────────────────────────

    /// <summary>
    /// The same three spellings <c>LiveStatusReply</c> has always used. Shared rather than
    /// reimplemented: two rungs answering the same question minutes apart must not spell a day two
    /// different ways, and "today so far" carries the partial-day warning that "today" does not.
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 4, "today so far")]
    [InlineData(2026, 9, 3, "yesterday")]
    [InlineData(2026, 8, 17, "Aug 17")]
    public void ADayIsSpelledTheOneWay(int year, int month, int day, string expected) =>
        Assert.Equal(expected, MemberChatReplies.DayLabel(new DateOnly(year, month, day), Today));

    /// <summary>
    /// A month name keeps its capital mid-sentence and the relative words do not — the casing
    /// <c>LiveStatusReplyTests</c> pins for the status rung, now guaranteed for every rung.
    /// </summary>
    [Fact]
    public void TheCasingIsTheSameOneTheStatusRungUses()
    {
        Assert.Equal("Aug 17", MemberChatReplies.DayLabel(new DateOnly(2026, 8, 17), Today));
        Assert.Equal("yesterday", MemberChatReplies.DayLabel(new DateOnly(2026, 9, 3), Today));
    }

    /// <summary>
    /// A span keeps the relative word at its far end, so a window ending today still says so and
    /// carries the "not finished yet" warning with it.
    /// </summary>
    [Fact]
    public void ASpanEndingTodayStillSaysTheDayIsUnfinished() =>
        Assert.Equal(
            "Aug 29 to today so far",
            MemberChatReplies.SpanLabel(new DateOnly(2026, 8, 29), Today, Today));

    [Fact]
    public void ASpanOfOneDayIsJustThatDay() =>
        Assert.Equal(
            "yesterday",
            MemberChatReplies.SpanLabel(new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Today));

    // ── What the model is allowed to have said ──────────────────────────────────

    [Fact]
    public void DatesInsideTheFetchedWindowResolve()
    {
        var span = MemberChatReplies.ResolveSpan("2026-09-01", "2026-09-03", Window);

        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3)), span);
    }

    /// <summary>
    /// Outside the window the answer was built from, the dates are dropped rather than trusted:
    /// the whitelist clamps what is fetched, so a date beyond it is a date no reading backs. Same
    /// posture as <c>ChatDataRegistry.CitationsFor</c> dropping an authority the registry does not
    /// carry — nothing claimed beats something invented.
    /// </summary>
    [Theory]
    [InlineData("2026-08-01", "2026-09-03")]  // starts before the window
    [InlineData("2026-09-01", "2026-09-30")]  // ends after it
    public void DatesOutsideTheFetchedWindowAreDropped(string from, string to) =>
        Assert.Null(MemberChatReplies.ResolveSpan(from, to, Window));

    /// <summary>
    /// No activity fetched means no figures to date. An answer built from member context and the
    /// baseline alone has no day to name, and naming one would invent it.
    /// </summary>
    [Fact]
    public void NothingFetchedMeansNothingToDate() =>
        Assert.Null(MemberChatReplies.ResolveSpan("2026-09-01", "2026-09-03", null));

    /// <summary>
    /// Strict yyyy-MM-dd. A lenient parse accepts "Sep 4" and fills the year in from the ambient
    /// culture, which is how a reply gets dated to a year with no readings in it.
    /// </summary>
    [Theory]
    [InlineData("Sep 1")]
    [InlineData("01/09/2026")]
    [InlineData("2026-9-1")]
    [InlineData("yesterday")]
    [InlineData("")]
    [InlineData(null)]
    public void ADateThisDoesNotRecogniseIsDropped(string? from) =>
        Assert.Null(MemberChatReplies.ResolveSpan(from, "2026-09-03", Window));

    /// <summary>
    /// A pair the wrong way round still says which two days were meant; the order is presentation,
    /// and correcting it costs nothing.
    /// </summary>
    [Fact]
    public void AReversedPairIsRighted() =>
        Assert.Equal(
            (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3)),
            MemberChatReplies.ResolveSpan("2026-09-03", "2026-09-01", Window));

    // ── What the caregiver reads ────────────────────────────────────────────────

    /// <summary>The transcript's failure, in one assertion: figures with no day get one.</summary>
    [Fact]
    public void AnUndatedReplyGetsItsDay()
    {
        var reply = MemberChatReplies.WithDayAttribution(
            "Dad had a stable day with 4,475 steps.", new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Today);

        Assert.Contains("Those figures are for yesterday.", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpanSaysItCoversRatherThanIsFor()
    {
        var reply = MemberChatReplies.WithDayAttribution(
            "His steps have held up.", new DateOnly(2026, 8, 29), Today, Today);

        Assert.Contains("Those figures cover Aug 29 to today so far.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// One statement is the framing, two is a stutter — the same conditional append
    /// <c>AdviseReply</c> uses to avoid naming the doctor twice in three sentences.
    /// </summary>
    [Theory]
    [InlineData("Yesterday Dad took 4,475 steps.")]
    [InlineData("That was yesterday's figure.")]
    public void AReplyThatAlreadyNamedTheDayIsLeftAlone(string original)
    {
        var reply = MemberChatReplies.WithDayAttribution(
            original, new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Today);

        Assert.Equal(original, reply);
        Assert.DoesNotContain("Those figures", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The marker comes from the validated window, never from the reply — so prose that says
    /// "today" about yesterday's figures is corrected rather than taken at its word. This is the
    /// exact shape of the observed failure: a reply that read as today, built from the last
    /// complete day.
    /// </summary>
    [Fact]
    public void AReplyThatNamedTheWrongDayIsCorrectedAnyway()
    {
        var reply = MemberChatReplies.WithDayAttribution(
            "Dad has had a stable day today with 4,475 steps.",
            new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Today);

        Assert.Contains("Those figures are for yesterday.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Today's own figures still say "today so far", because a running total is not a finished
    /// day and a caregiver comparing two replies needs to know which of them was still moving.
    /// </summary>
    [Fact]
    public void TodaysFiguresAreMarkedUnfinished()
    {
        var reply = MemberChatReplies.WithDayAttribution(
            "He's on 4,905 steps.", Today, Today, Today);

        Assert.Contains("Those figures are for today so far.", reply, StringComparison.Ordinal);
    }
}
