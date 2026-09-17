using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the arithmetic and the vocabulary the journal rung does in code: which stored date a
/// named day resolves to for each book, which periods can have a book at all, what counts as a
/// yes, and that the line held on the session survives a round trip.
/// </summary>
public class JournalChatRequestTests
{
    /// <summary>Wednesday 16 September 2026, the member's local today in most cases below.</summary>
    private static readonly DateOnly Today = new(2026, 9, 16);

    // ── Period arithmetic ───────────────────────────────────────────────────

    [Fact]
    public void A_daybook_is_dated_by_the_day_itself()
    {
        var end = JournalChatRequest.PeriodEndContaining(new DateOnly(2026, 9, 10), DigestAudience.Daybook, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 9, 10), end);
    }

    /// <summary>
    /// A Monday-start journal week ends on Sunday. Thursday the 10th sits in the week that ends
    /// Sunday the 13th — the date the stored Weekbook carries.
    /// </summary>
    [Fact]
    public void A_weekbook_is_dated_by_the_last_day_of_the_members_week()
    {
        var end = JournalChatRequest.PeriodEndContaining(new DateOnly(2026, 9, 10), DigestAudience.Weekbook, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 9, 13), end);
    }

    /// <summary>
    /// The same Thursday for a Sunday-start member belongs to the week ending Saturday the 12th.
    /// The arithmetic is the member's, not the calendar's — which is why it is not the model's.
    /// </summary>
    [Fact]
    public void A_weekbook_honours_the_members_own_week_start()
    {
        var end = JournalChatRequest.PeriodEndContaining(new DateOnly(2026, 9, 10), DigestAudience.Weekbook, DayOfWeek.Sunday);

        Assert.Equal(new DateOnly(2026, 9, 12), end);
    }

    [Fact]
    public void A_day_already_on_the_weeks_last_day_stays_put()
    {
        var end = JournalChatRequest.PeriodEndContaining(new DateOnly(2026, 9, 13), DigestAudience.Weekbook, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 9, 13), end);
    }

    [Fact]
    public void A_monthbook_is_dated_by_the_months_last_day()
    {
        var end = JournalChatRequest.PeriodEndContaining(new DateOnly(2026, 2, 3), DigestAudience.Monthbook, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 2, 28), end);
    }

    [Fact]
    public void The_family_series_is_not_a_book()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JournalChatRequest.PeriodEndContaining(Today, DigestAudience.Family, DayOfWeek.Monday));
    }

    // ── Which periods can have a book ───────────────────────────────────────

    [Fact]
    public void Yesterday_is_finished()
    {
        Assert.True(JournalChatRequest.IsFinished(Today.AddDays(-1), Today));
    }

    /// <summary>
    /// Today is not over, and neither is the week or month that ends today: a book for a period
    /// ending today would describe hours that have not happened.
    /// </summary>
    [Fact]
    public void A_period_ending_today_or_later_is_not_finished()
    {
        Assert.False(JournalChatRequest.IsFinished(Today, Today));
        Assert.False(JournalChatRequest.IsFinished(Today.AddDays(4), Today));
    }

    /// <summary>
    /// No lower bound: how far back a book can reach is the data's to answer, not a constant's
    /// that would have to track the partition worker's retention setting.
    /// </summary>
    [Fact]
    public void A_period_long_ago_is_still_finished()
    {
        Assert.True(JournalChatRequest.IsFinished(Today.AddYears(-3), Today));
    }

    // ── The resolution vocabulary ───────────────────────────────────────────

    [Theory]
    [InlineData("rewrite", JournalChatAction.Rewrite)]
    [InlineData("Regenerate", JournalChatAction.Rewrite)]
    [InlineData("discard", JournalChatAction.Discard)]
    [InlineData("delete", JournalChatAction.Discard)]
    [InlineData("show", JournalChatAction.Show)]
    [InlineData("list", JournalChatAction.List)]
    public void Actions_parse_from_the_closed_labels(string label, JournalChatAction expected)
    {
        Assert.Equal(expected, JournalChatRequest.ParseAction(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("summarise")]
    public void An_unknown_action_is_nothing(string? label)
    {
        Assert.Null(JournalChatRequest.ParseAction(label));
    }

    [Theory]
    [InlineData("day", DigestAudience.Daybook)]
    [InlineData("Weekbook", DigestAudience.Weekbook)]
    [InlineData("month", DigestAudience.Monthbook)]
    public void Cadences_parse_to_the_book(string label, DigestAudience expected)
    {
        Assert.Equal(expected, JournalChatRequest.ParseCadence(label));
    }

    /// <summary>A date the model garbles costs the period, not the turn.</summary>
    [Theory]
    [InlineData("2026-09-10", true)]
    [InlineData("10 September", false)]
    [InlineData("2026-9-10", false)]
    [InlineData(null, false)]
    public void Only_a_strict_iso_date_is_a_date(string? value, bool parses)
    {
        Assert.Equal(parses, JournalChatRequest.ParseDate(value) is not null);
    }

    // ── The confirmation vocabulary ─────────────────────────────────────────

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, please!")]
    [InlineData("  go ahead ")]
    [InlineData("OK")]
    [InlineData("do it.")]
    public void A_plain_yes_is_affirmative(string message)
    {
        Assert.True(JournalChatRequest.IsAffirmative(message));
        Assert.False(JournalChatRequest.IsNegative(message));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("leave it")]
    [InlineData("never mind.")]
    public void A_plain_no_is_negative(string message)
    {
        Assert.True(JournalChatRequest.IsNegative(message));
        Assert.False(JournalChatRequest.IsAffirmative(message));
    }

    /// <summary>
    /// A yes with anything else attached is a new message, not consent: "yes but how did he
    /// sleep" is routed, and the offer is spent unanswered.
    /// </summary>
    [Theory]
    [InlineData("yes but how did he sleep")]
    [InlineData("yes delete the weekbook too")]
    [InlineData("how did he sleep")]
    public void Anything_more_than_a_yes_or_a_no_is_neither(string message)
    {
        Assert.False(JournalChatRequest.IsAffirmative(message));
        Assert.False(JournalChatRequest.IsNegative(message));
    }

    // ── The line the session carries ────────────────────────────────────────

    [Fact]
    public void A_pending_request_survives_the_round_trip()
    {
        var request = new JournalChatRequest(JournalChatAction.Rewrite, DigestAudience.Weekbook, new DateOnly(2026, 9, 13));

        var stored = request.Serialize();

        Assert.Equal("Rewrite|Weekbook|2026-09-13", stored);
        Assert.Equal(request, JournalChatRequest.TryDeserialize(stored));
    }

    [Fact]
    public void Only_a_dated_destructive_request_can_be_pending()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new JournalChatRequest(JournalChatAction.Show, DigestAudience.Daybook, Today).Serialize());
        Assert.Throws<InvalidOperationException>(() =>
            new JournalChatRequest(JournalChatAction.Discard, DigestAudience.Daybook, null).Serialize());
    }

    /// <summary>A line an older build wrote, or a corrupted one, is simply nothing pending.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Rewrite|Weekbook")]
    [InlineData("Show|Daybook|2026-09-13")]
    [InlineData("Rewrite|Family|2026-09-13")]
    [InlineData("Rewrite|Weekbook|tomorrow")]
    [InlineData("99|Weekbook|2026-09-13")]
    public void A_malformed_line_is_nothing_pending(string? stored)
    {
        Assert.Null(JournalChatRequest.TryDeserialize(stored));
    }
}
