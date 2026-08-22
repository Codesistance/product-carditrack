using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Where last night lives in a readings block, and what happens when it has not arrived. Sleep
/// sessions are attributed to the civil day they <em>ended</em> on, so last night's figure sits on
/// <em>today's</em> row — a two-step inference the model does not reliably make, and the label is
/// the only place the block states it.
/// <para>
/// Both halves of that failed at once in one conversation. Asked "how did they sleep last night"
/// member chat answered correctly that it had no reading for it; asked a broader question a minute
/// later, over a window whose newest row was yesterday's, it called the night before last "last
/// night" and gave it in hours — two contradictory answers four minutes apart.
/// </para>
/// </summary>
public class DailyLinesTodayAnchorTests
{
    private static readonly DateOnly Today = new(2026, 8, 22);

    private static ActivityLog Log(DateOnly date, int? steps = null, int? sleep = null) =>
        new() { Date = date, Steps = steps, SleepMinutes = sleep };

    /// <summary>
    /// A missing today row used to mean no today line at all, which left the newest sleep figure
    /// on the block belonging to a night nobody had asked about and nothing saying so.
    /// </summary>
    [Fact]
    public void TodaysRowIsWritten_EvenWhenNoReadingHasArrivedForIt()
    {
        var lines = MedicalPromptBlocks.DailyLines(
            [Log(Today.AddDays(-2), steps: 3100, sleep: 400), Log(Today.AddDays(-1), steps: 2800, sleep: 375)],
            take: 7,
            Today);

        Assert.Contains($"Today so far ({Today}", lines);
        Assert.Contains("last night's sleep belongs on this row and has not arrived", lines);
    }

    /// <summary>
    /// "not measured" in every column, never a zero — the reason <c>Figure</c> exists at all. An
    /// empty value is the one answer that cannot be read as "it did not".
    /// </summary>
    [Fact]
    public void TheAnchorReadsNotMeasured_NeverZero()
    {
        var lines = MedicalPromptBlocks.DailyLines([Log(Today.AddDays(-1), steps: 2800)], take: 7, Today);

        var todayLine = Assert.Single(lines.Split('\n'), l => l.Contains("Today so far"));
        Assert.Contains("steps=not measured, HR=not measured", todayLine);
        Assert.Contains("sleep(night ending that morning)=not measured", todayLine);
        Assert.DoesNotContain("=0", todayLine);
    }

    /// <summary>The real row wins; nothing is synthesised beside it.</summary>
    [Fact]
    public void ARealTodayRowIsNotDuplicated()
    {
        var lines = MedicalPromptBlocks.DailyLines(
            [Log(Today.AddDays(-1), steps: 2800), Log(Today, steps: 900, sleep: 372)], take: 7, Today);

        Assert.Single(lines.Split('\n'), l => l.Contains("Today so far"));
        Assert.Contains("sleep(night ending that morning)=6h 12m", lines);
    }

    /// <summary>
    /// A label may not describe a reading the row does not have. Today's label used to announce
    /// "the sleep figure is last night's and complete" unconditionally, so a row the watch had not
    /// filled in yet claimed a complete figure on the same line that gave it as "not measured" —
    /// and the label is the more emphatic of the two.
    /// </summary>
    [Fact]
    public void TodaysLabelDoesNotClaimASleepFigureThatIsNotThere()
    {
        var lines = MedicalPromptBlocks.DailyLines([Log(Today, steps: 900)], take: 7, Today);

        Assert.Contains("last night's sleep belongs on this row and has not arrived", lines);
        Assert.DoesNotContain("complete", lines);
    }

    /// <summary>And still says it plainly when the figure is there, which is the common case.</summary>
    [Fact]
    public void TodaysLabelStillSaysTheFigureIsLastNightsWhenItHasArrived()
    {
        var lines = MedicalPromptBlocks.DailyLines([Log(Today, steps: 900, sleep: 372)], take: 7, Today);

        Assert.Contains("the sleep figure is last night's and complete", lines);
    }

    /// <summary>
    /// The anchor is a statement about today, not one of the N most recent readings. Member chat
    /// passes the row count as <c>take</c>, so counting the anchor against it would drop the oldest
    /// day from the answer while leaving it on the chart drawn beneath.
    /// </summary>
    [Fact]
    public void TheAnchorIsNotCountedAgainstTake()
    {
        var rows = new[]
        {
            Log(Today.AddDays(-3), steps: 1111),
            Log(Today.AddDays(-2), steps: 2222),
            Log(Today.AddDays(-1), steps: 3333),
        };

        var lines = MedicalPromptBlocks.DailyLines(rows, take: rows.Length, Today);

        Assert.Contains("steps=1111", lines);
        Assert.Contains("steps=3333", lines);
        Assert.Equal(4, lines.Split('\n').Length);
    }

    /// <summary>
    /// A member with no readings at all gets nothing synthesised. "No recent activity data." says
    /// it already, and better than a row of "not measured" would.
    /// </summary>
    [Fact]
    public void NothingIsSynthesisedForAMemberWithNoReadingsAtAll()
    {
        Assert.Equal("No recent activity data.", MedicalPromptBlocks.DailyLines([], take: 7, Today));
    }

    // ── The family digest's own rows ────────────────────────────────────────────
    //
    // Same anchor, same reason, and it needs its own: the digest fetches exactly yesterday and
    // today, so a member whose watch has not synced this morning is left with a single row —
    // yesterday's — whose sleep figure is then the only night in the prompt.

    [Fact]
    public void TheDigestAlsoWritesTodaysRow_WhenNoReadingHasArrived()
    {
        var lines = MedicalPromptBlocks.FamilyDigestDailyLines(
            [Log(Today.AddDays(-1), steps: 3835, sleep: 400)], Today);

        Assert.Contains($"Today so far ({Today}", lines);
        Assert.Contains("last night's sleep belongs on this row and has not arrived", lines);
        Assert.Contains("steps=not measured", lines);
    }

    [Fact]
    public void TheDigestDoesNotDuplicateARealTodayRow()
    {
        var lines = MedicalPromptBlocks.FamilyDigestDailyLines(
            [Log(Today.AddDays(-1), steps: 3835), Log(Today, steps: 3442, sleep: 372)], Today);

        Assert.Single(lines.Split('\n'), l => l.Contains("Today so far"));
        Assert.Contains("the sleep figure is last night's and complete", lines);
    }

    [Fact]
    public void TheDigestSynthesisesNothingForAMemberWithNoReadingsAtAll()
    {
        Assert.Equal("No recent activity data.", MedicalPromptBlocks.FamilyDigestDailyLines([], Today));
    }
}
