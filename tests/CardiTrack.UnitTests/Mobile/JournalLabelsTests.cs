using System.Globalization;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Pins how a CardiJournal entry names its own period. A Daybook says the day the way a person
/// says it; a Weekbook names both ends of its span, because its date is the week's last day and a
/// caregiver should not have to do the arithmetic to know which week they are reading.
/// </summary>
/// <remarks>
/// The labels format against <see cref="CultureInfo.CurrentCulture"/> on purpose — a caregiver
/// reading in French should get French weekday names — so these assertions about English wording
/// only hold under a pinned culture. Set here and restored after, rather than asserting with
/// <see cref="CultureInfo.InvariantCulture"/>, which would test a formatting path the app never
/// takes.
/// </remarks>
public class JournalLabelsTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    public JournalLabelsTests() => CultureInfo.CurrentCulture = new CultureInfo("en-GB");

    public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;

    /// <summary>Monday 10 August 2026.</summary>
    private static readonly DateOnly Today = new(2026, 8, 10);

    // ── Days ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_day_just_gone_is_yesterday()
        => Assert.Equal("Yesterday", JournalLabels.DayLabel(Today.AddDays(-1), Today));

    [Fact]
    public void Today_says_so()
        => Assert.Equal("Today", JournalLabels.DayLabel(Today, Today));

    [Fact]
    public void Earlier_in_the_week_is_named_by_its_weekday()
        => Assert.Equal("Friday", JournalLabels.DayLabel(Today.AddDays(-3), Today));

    [Fact]
    public void Beyond_the_week_is_named_by_its_date()
        => Assert.Equal("27 July", JournalLabels.DayLabel(new DateOnly(2026, 7, 27), Today));

    // ── Weeks ───────────────────────────────────────────────────────────────

    /// <summary>The entry's date is the week's last day, so the span runs back six days from it.</summary>
    [Fact]
    public void A_week_names_both_of_its_ends()
        => Assert.Equal("Mon 3 – Sun 9 August", JournalLabels.WeekLabel(new DateOnly(2026, 8, 9)));

    [Fact]
    public void A_week_spanning_two_months_names_both_months()
        => Assert.Equal("Mon 27 July – Sun 2 August", JournalLabels.WeekLabel(new DateOnly(2026, 8, 2)));

    /// <summary>
    /// No relative wording on a week. "Last week" is ambiguous the moment a caregiver scrolls to a
    /// fortnight-old entry, where "Yesterday" on a day never is.
    /// </summary>
    [Fact]
    public void The_most_recent_week_is_still_named_by_its_span()
    {
        var label = JournalLabels.WeekLabel(Today.AddDays(-1));

        Assert.Equal("Mon 3 – Sun 9 August", label);
        Assert.DoesNotContain("Last", label);
        Assert.DoesNotContain("Yesterday", label);
    }

    // ── Culture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The English wording above is a property of the pinned culture, not of the labels. A
    /// caregiver reading in another language gets their own weekday and month names — which is
    /// why the source formats against CurrentCulture and why these tests pin it rather than
    /// asserting invariant output.
    /// </summary>
    [Fact]
    public void The_labels_follow_the_readers_culture()
    {
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

        Assert.Equal("vendredi", JournalLabels.DayLabel(Today.AddDays(-3), Today));
        Assert.Contains("août", JournalLabels.WeekLabel(new DateOnly(2026, 8, 9)));
    }

    // ── Dispatch ────────────────────────────────────────────────────────────

    [Fact]
    public void The_cadence_decides_which_label_is_used()
    {
        var date = new DateOnly(2026, 8, 9);

        Assert.Equal(
            JournalLabels.DayLabel(date, Today),
            JournalLabels.PeriodLabel(JournalCadence.Daybook, date, Today));

        Assert.Equal(
            JournalLabels.WeekLabel(date),
            JournalLabels.PeriodLabel(JournalCadence.Weekbook, date, Today));
    }
}

/// <summary>
/// Pins the cadence's wire vocabulary and the words it lends the UI. The wire values are an API
/// contract — <c>?audience=</c> on the insights endpoints refuses anything it does not recognise.
/// </summary>
public class JournalCadenceTests
{
    [Theory]
    [InlineData(JournalCadence.Daybook, "daybook")]
    [InlineData(JournalCadence.Weekbook, "weekbook")]
    public void Wire_values_match_the_audiences_the_api_accepts(JournalCadence cadence, string expected)
        => Assert.Equal(expected, cadence.WireValue());

    [Theory]
    [InlineData(JournalCadence.Daybook, "Daybook", "Days")]
    [InlineData(JournalCadence.Weekbook, "Weekbook", "Weeks")]
    public void An_entry_is_named_for_its_book_and_a_segment_for_its_period(
        JournalCadence cadence, string entryName, string segmentLabel)
    {
        Assert.Equal(entryName, cadence.EntryName());
        Assert.Equal(segmentLabel, cadence.SegmentLabel());
    }

    [Theory]
    [InlineData("weekbook", JournalCadence.Weekbook)]
    [InlineData("WEEKBOOK", JournalCadence.Weekbook)]
    [InlineData("daybook", JournalCadence.Daybook)]
    public void A_cadence_round_trips_through_its_wire_value(string wire, JournalCadence expected)
        => Assert.Equal(expected, JournalCadenceExtensions.ParseCadence(wire));

    /// <summary>
    /// A link written before the cadence parameter existed carries none, and must still open the
    /// series it was written for rather than failing or landing on the wrong book.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("monthbook")]
    public void An_absent_or_unknown_cadence_falls_back_to_the_daybook(string? wire)
        => Assert.Equal(JournalCadence.Daybook, JournalCadenceExtensions.ParseCadence(wire));
}
