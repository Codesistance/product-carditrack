using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins what the Monthbook prompt tells the model about a month: averages over the days actually
/// measured, how many those were, and the <em>week</em> that sat furthest out — computed here,
/// never left to the model. At month scale the standout is a week, not a day: a single unusual day
/// is noise a caregiver has already read in its Daybook.
/// </summary>
public class MonthbookPromptTests
{
    private static readonly DateOnly MonthStart = new(2026, 7, 1);
    private static readonly DateOnly MonthEnd = new(2026, 7, 31);

    private static ActivityLog Day(DateOnly date, int? steps = null, int? restingHeartRate = null) =>
        new() { Date = date, Steps = steps, RestingHeartRate = restingHeartRate };

    private static List<ActivityLog> Month(Func<int, int?> steps, int days = 31) =>
        Enumerable.Range(0, days).Select(i => Day(MonthStart.AddDays(i), steps(i))).ToList();

    // ── Readings ────────────────────────────────────────────────────────────

    [Fact]
    public void An_average_is_over_the_days_that_carried_the_reading()
    {
        var days = Month(i => i < 10 ? 4000 : null);

        var section = MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("4,000 steps on average", section);
        Assert.Contains("measured on 10 days of the month", section);
    }

    [Fact]
    public void A_month_with_nothing_measured_renders_no_section()
    {
        var days = Month(_ => null);

        Assert.Equal(string.Empty, MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    [Fact]
    public void The_members_own_usual_is_stated_when_a_baseline_exists()
    {
        var days = Month(_ => 5000);
        var baseline = new PatternBaseline { AvgSteps = 4000 };

        Assert.Contains("Their usual is 4,000 steps",
            MonthbookPrompt.ReadingsSection(days, baseline, ageYears: 70));
    }

    [Fact]
    public void A_published_band_names_who_publishes_it()
    {
        var days = Enumerable.Range(0, 31)
            .Select(i => Day(MonthStart.AddDays(i), restingHeartRate: 62)).ToList();

        Assert.Contains("The published range is 60-100 bpm (AHA)",
            MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    // ── The standout week ───────────────────────────────────────────────────

    /// <summary>The month's shape is which week ran differently, not which day did.</summary>
    [Fact]
    public void The_week_furthest_from_the_months_average_is_named()
    {
        // Days 14–20 (the third seven-day block) sit far below the rest.
        var days = Month(i => i is >= 14 and <= 20 ? 500 : 6000);

        var section = MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("Furthest from the month's own average: the week of 15 July", section);
    }

    [Fact]
    public void An_even_month_names_no_standout_week()
    {
        var days = Month(i => 5000 + i);

        Assert.DoesNotContain("Furthest from",
            MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    /// <summary>
    /// A single wild day is not a shape. It moves the month's average barely at all, and its own
    /// week stays inside the threshold — which is the point of aggregating to weeks first.
    /// </summary>
    [Fact]
    public void One_unusual_day_does_not_make_a_standout_week()
    {
        var days = Month(i => i == 17 ? 0 : 6000);

        Assert.DoesNotContain("Furthest from",
            MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    [Fact]
    public void A_month_without_three_measured_weeks_claims_no_standout()
    {
        // Twelve days — two full week-blocks and a stub, so no week can be called the odd one.
        var days = Month(i => i < 12 ? (i < 6 ? 500 : 6000) : null);

        Assert.DoesNotContain("Furthest from",
            MonthbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    // ── Coverage ────────────────────────────────────────────────────────────

    [Fact]
    public void A_fully_measured_month_says_so()
    {
        var line = MonthbookPrompt.CoverageLine(Month(_ => 1), MonthStart, MonthEnd);

        Assert.Contains("July 2026", line);
        Assert.Contains("every one of its 31 days carried readings", line);
    }

    /// <summary>Silence must never read as healthy — the gap is stated.</summary>
    [Fact]
    public void A_partial_month_states_how_much_was_measured()
    {
        var days = Month(i => 1, days: 20);

        var line = MonthbookPrompt.CoverageLine(days, MonthStart, MonthEnd);

        Assert.Contains("20 of its 31 days carried readings", line);
        Assert.Contains("the rest were not measured", line);
    }

    // ── Monitoring ──────────────────────────────────────────────────────────

    [Fact]
    public void A_quiet_month_renders_no_monitoring_section()
        => Assert.Equal(string.Empty, MonthbookPrompt.MonitoringSection([], []));

    /// <summary>Ordinary assessments are not "observed" — the Weekbook's rule, at month scale.</summary>
    [Fact]
    public void Only_yellow_and_above_are_counted_as_observed()
    {
        var assessments = new List<RealtimeAssessment>
        {
            new() { Severity = AlertSeverity.Green },
            new() { Severity = AlertSeverity.Yellow },
            new() { Severity = AlertSeverity.Red },
            new() { Severity = null },
        };

        Assert.Contains("2 hours were observed as worth noting",
            MonthbookPrompt.MonitoringSection([], assessments));
    }

    [Fact]
    public void Alerts_are_counted_never_scored()
    {
        var alerts = new List<Alert>
        {
            new() { Severity = AlertSeverity.Orange, IsResolved = true },
            new() { Severity = AlertSeverity.Orange, IsResolved = false },
        };

        var section = MonthbookPrompt.MonitoringSection(alerts, []);

        Assert.Contains("2 alerts were raised during the month", section);
        Assert.Contains("1 still unresolved", section);
    }

    // ── The shared register ─────────────────────────────────────────────────

    [Fact]
    public void The_register_guards_still_bite()
    {
        Assert.NotNull(MonthbookPrompt.NamesACondition("A month showing signs of sleep apnoea."));
        Assert.NotNull(MonthbookPrompt.UnglossedTerm("Her sleep efficiency held all month."));
        Assert.True(MonthbookPrompt.ReadsLikeTheInstructions(
            "Say what held across the whole month, and what changed within it."));
    }

    /// <summary>
    /// Each book is checked against its own brief. A Weekbook's phrasing must not discard a
    /// Monthbook, or the two prompts could never diverge.
    /// </summary>
    [Fact]
    public void The_weekbooks_own_phrasing_does_not_trip_the_monthbook()
        => Assert.False(MonthbookPrompt.ReadsLikeTheInstructions(
            "A list of seven days is not an account of a week."));

    [Fact]
    public void An_ordinary_account_survives_every_guard()
    {
        const string text =
            "Ada's July held together well. She averaged 6h 50m of sleep across 28 measured "
            + "nights, close to her usual 7h. The week of 15 July stood apart — she walked far "
            + "less then, averaging 500 steps a day against roughly 6,000 for the rest of the "
            + "month. Her resting heart rate stayed near 62 bpm throughout, inside the published "
            + "range.";

        Assert.False(MonthbookPrompt.ReadsLikeTheInstructions(text));
        Assert.Null(MonthbookPrompt.NamesACondition(text));
        Assert.Null(MonthbookPrompt.UnglossedTerm(text));
    }
}
