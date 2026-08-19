using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins what the Weekbook prompt tells the model about a week: averages over the days that were
/// actually measured, how many those were, and the day that sat furthest out — all computed here,
/// never left to the model. Also pins that the shared register guards still bite.
/// </summary>
public class WeekbookPromptTests
{
    private static readonly DateOnly WeekStart = new(2026, 8, 10); // Monday
    private static readonly DateOnly WeekEnd = new(2026, 8, 16);   // Sunday

    private static ActivityLog Day(DateOnly date, int? steps = null, int? sleepMinutes = null,
        int? restingHeartRate = null) => new()
        {
            Date = date,
            Steps = steps,
            SleepMinutes = sleepMinutes,
            RestingHeartRate = restingHeartRate,
        };

    private static List<ActivityLog> Week(Func<int, ActivityLog> build, int days = 7) =>
        Enumerable.Range(0, days).Select(build).ToList();

    // ── Readings ────────────────────────────────────────────────────────────

    [Fact]
    public void An_average_is_over_the_days_that_carried_the_reading_not_the_whole_week()
    {
        // Two days at 1000 steps; the other five never measured. The average is 1000, not 2000/7.
        var days = new List<ActivityLog>
        {
            Day(WeekStart, steps: 1000),
            Day(WeekStart.AddDays(1), steps: 1000),
            Day(WeekStart.AddDays(2)),
            Day(WeekStart.AddDays(3)),
        };

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("1,000 steps on average", section);
        Assert.Contains("measured on 2 days of the week", section);
    }

    [Fact]
    public void A_metric_measured_on_no_day_is_absent_rather_than_zero()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 5000));

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("Steps:", section);
        // Nothing carried a resting heart rate, so the week says nothing about one.
        Assert.DoesNotContain("Resting heart rate", section);
    }

    [Fact]
    public void A_week_with_nothing_measured_at_all_renders_no_section()
    {
        var days = Week(i => Day(WeekStart.AddDays(i)));

        Assert.Equal(string.Empty, WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70));
    }

    [Fact]
    public void The_members_own_usual_is_stated_when_a_baseline_exists()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 5000));
        var baseline = new PatternBaseline { AvgSteps = 4000 };

        var section = WeekbookPrompt.ReadingsSection(days, baseline, ageYears: 70);

        Assert.Contains("Their usual is 4,000 steps", section);
    }

    [Fact]
    public void Without_a_baseline_no_usual_is_invented()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 5000));

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.DoesNotContain("Their usual", section);
    }

    /// <summary>The band is quoted with its publisher, per the standing rule.</summary>
    [Fact]
    public void A_published_band_names_who_publishes_it()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), restingHeartRate: 62));

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("The published range is 60-100 bpm (AHA)", section);
    }

    // ── The standout day ────────────────────────────────────────────────────

    [Fact]
    public void The_day_furthest_from_the_weeks_average_is_named()
    {
        // Six days near 5000, one at 500 — far outside a fifth of the average.
        var days = Week(i => Day(WeekStart.AddDays(i), steps: i == 3 ? 500 : 5000));

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.Contains("Furthest from the week's own average: Thursday 13 August", section);
    }

    [Fact]
    public void An_even_week_has_no_standout_day()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 5000 + i));

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.DoesNotContain("Furthest from", section);
    }

    /// <summary>"The odd one out" of three days is a coin toss, so it is not claimed.</summary>
    [Fact]
    public void A_week_too_thin_to_have_a_standout_does_not_claim_one()
    {
        var days = new List<ActivityLog>
        {
            Day(WeekStart, steps: 5000),
            Day(WeekStart.AddDays(1), steps: 5000),
            Day(WeekStart.AddDays(2), steps: 100),
        };

        var section = WeekbookPrompt.ReadingsSection(days, baseline: null, ageYears: 70);

        Assert.DoesNotContain("Furthest from", section);
    }

    // ── Coverage ────────────────────────────────────────────────────────────

    [Fact]
    public void A_full_week_says_so()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 1));

        var line = WeekbookPrompt.CoverageLine(days, WeekStart, WeekEnd);

        Assert.Contains("Monday 10 August to Sunday 16 August", line);
        Assert.Contains("every day carried readings", line);
    }

    /// <summary>Silence must never read as healthy — the gap is stated, not implied.</summary>
    [Fact]
    public void A_partial_week_states_how_much_of_it_was_measured()
    {
        var days = Week(i => Day(WeekStart.AddDays(i), steps: 1), days: 5);

        var line = WeekbookPrompt.CoverageLine(days, WeekStart, WeekEnd);

        Assert.Contains("5 of its 7 days carried readings", line);
        Assert.Contains("the rest were not measured", line);
    }

    // ── Monitoring ──────────────────────────────────────────────────────────

    [Fact]
    public void A_quiet_week_renders_no_monitoring_section()
    {
        Assert.Equal(string.Empty, WeekbookPrompt.MonitoringSection([], []));
    }

    [Fact]
    public void Alerts_are_counted_never_scored()
    {
        var alerts = new List<Alert>
        {
            new() { Severity = AlertSeverity.Yellow, IsResolved = true },
            new() { Severity = AlertSeverity.Yellow, IsResolved = false },
        };

        var section = WeekbookPrompt.MonitoringSection(alerts, []);

        Assert.Contains("2 alerts were raised during the week", section);
        Assert.Contains("1 still unresolved", section);
        Assert.Contains("2 yellow alerts", section);
    }

    // ── The shared register ─────────────────────────────────────────────────

    [Fact]
    public void A_reply_naming_a_condition_is_caught()
    {
        Assert.NotNull(WeekbookPrompt.NamesACondition(
            "Her resting heart rate was a sign of atrial fibrillation this week."));
    }

    [Fact]
    public void A_reply_proposing_a_treatment_is_caught()
    {
        Assert.NotNull(WeekbookPrompt.NamesACondition(
            "She should take a higher dosage in the evenings."));
    }

    [Fact]
    public void A_precise_term_must_explain_itself()
    {
        Assert.Equal("sleep efficiency", WeekbookPrompt.UnglossedTerm(
            "Her sleep efficiency held steady across the week."));

        Assert.Null(WeekbookPrompt.UnglossedTerm(
            "Her sleep efficiency — the share of time in bed actually spent asleep — held steady."));
    }

    [Fact]
    public void A_reply_that_restates_the_weeks_own_brief_is_caught()
    {
        Assert.True(WeekbookPrompt.ReadsLikeTheInstructions(
            "A list of seven days is not an account of a week."));
    }

    /// <summary>
    /// The echo list is per-book: the Weekbook must not be discarded for a phrase that only
    /// appears in the Daybook's brief, or the two prompts could never diverge.
    /// </summary>
    [Fact]
    public void The_daybooks_own_phrasing_does_not_trip_the_weekbook()
    {
        Assert.False(WeekbookPrompt.ReadsLikeTheInstructions(
            "Use them to say when in the day things happened."));
    }

    [Fact]
    public void An_ordinary_account_survives_every_guard()
    {
        const string text =
            "Ada slept a little less than usual this week, averaging 6h 40m across six measured "
            + "nights against her usual 7h 10m. Thursday stood apart, at 4h 05m. Her resting heart "
            + "rate held steady near 62 bpm all week, inside the published range. She walked most "
            + "days, and the quieter Thursday was the one that pulled the week's average down.";

        Assert.False(WeekbookPrompt.ReadsLikeTheInstructions(text));
        Assert.Null(WeekbookPrompt.NamesACondition(text));
        Assert.Null(WeekbookPrompt.UnglossedTerm(text));
    }
}
