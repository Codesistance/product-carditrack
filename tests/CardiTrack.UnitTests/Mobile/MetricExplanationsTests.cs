using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// What a Key Metric Trends card says it is comparing a reading against. The rule these pin is
/// that the copy follows the chart: a card only claims a dashed rule or a shaded band when the
/// data behind it actually draws one, and the legend, the footer and the "i" panel all call the
/// rule the same thing.
/// </summary>
public class MetricExplanationsTests
{
    private static DashboardMetric Metric(decimal? baseline = null, MetricReference? reference = null) =>
        new() { Baseline = baseline, Reference = reference };

    private static MetricReference Reference(decimal low, decimal high, string source) =>
        new() { Low = low, High = high, Source = source };

    // ---- The defect this file exists for: promising a mark that is not on the chart ----

    [Fact]
    public void A_metric_still_learning_its_baseline_does_not_promise_dashes()
    {
        // The screenshot case: steps, no learned baseline yet, so the chart draws no dashed rule.
        var (footer, panel) = MetricExplanations.For("Activity", Metric(), "{0:N0}");

        Assert.DoesNotContain("Dashes", footer);
        Assert.Equal("Still learning their usual day.", footer);
        Assert.Contains("no dashed line", panel);
    }

    [Fact]
    public void A_metric_with_a_baseline_names_the_dashes()
    {
        var (footer, panel) = MetricExplanations.For("Activity", Metric(baseline: 5432), "{0:N0}");

        Assert.Equal("Dashes: their usual day.", footer);
        Assert.Contains("The dashed line is", panel);
    }

    [Theory]
    [InlineData("Activity")]
    [InlineData("Heart Rate")]
    [InlineData("Sleep")]
    [InlineData("Skin Temp")]
    [InlineData("Something we have not written copy for")]
    public void No_metric_mentions_dashes_without_a_baseline_to_draw_them_from(string name)
    {
        var (footer, panel) = MetricExplanations.For(name, Metric(), "{0:0.#}");

        Assert.DoesNotContain("Dashes:", footer);
        Assert.DoesNotContain("The dashed line is", panel);
    }

    [Fact]
    public void A_metric_with_no_reference_range_does_not_promise_a_band()
    {
        // Steps: no standards body publishes one, so the chart shades nothing whatever the member's
        // own history looks like.
        var (footer, panel) = MetricExplanations.For("Activity", Metric(baseline: 5432), "{0:N0}");

        Assert.DoesNotContain("Band", footer);
        Assert.Contains("no shaded band", panel);
    }

    // ---- The key names the same mark the footer and the panel do ----

    [Theory]
    [InlineData("Activity", "Their usual day")]
    [InlineData("Heart Rate", "Their usual")]
    [InlineData("Sleep", "Their usual night")]
    [InlineData("Skin Temp", "Their nightly normal")]
    public void The_legend_key_is_the_footer_phrase_in_sentence_case(string name, string expected)
    {
        Assert.Equal(expected, MetricExplanations.BaselineLabel(name));

        // The same words, so the caregiver reading the key and the caregiver reading the strip
        // under the chart are being told about one mark rather than two.
        var (footer, _) = MetricExplanations.For(name, Metric(baseline: 70), "{0:0.#}");
        Assert.Contains(expected.ToLowerInvariant(), footer);
    }

    [Fact]
    public void A_metric_with_no_copy_still_gets_a_key()
    {
        Assert.Equal("Their own normal", MetricExplanations.BaselineLabel("Grip Strength"));
    }

    // ---- Metrics that never learn a baseline say so, rather than saying "still learning" ----

    [Theory]
    [InlineData("Blood Oxygen")]
    [InlineData("Breathing Rate")]
    public void A_metric_with_no_baseline_concept_does_not_claim_to_be_learning_one(string name)
    {
        var (footer, _) = MetricExplanations.For(
            name, Metric(reference: Reference(95, 100, "WHO")), "{0:0.#}");

        Assert.DoesNotContain("Still learning", footer);
        Assert.StartsWith("No personal normal for this reading.", footer);
    }

    // ---- The band is quoted from the metric, never written into the copy ----

    [Fact]
    public void The_band_is_read_off_the_metric_rather_than_written_into_the_copy()
    {
        var sixtyFive = MetricExplanations.For(
            "Sleep", Metric(baseline: 6.5m, reference: Reference(7, 8, "NHS")), "{0:0.#}");
        var overSixtyFive = MetricExplanations.For(
            "Sleep", Metric(baseline: 6.5m, reference: Reference(6, 7, "NHS")), "{0:0.#}");

        // Sleep's recommendation drops by an hour from age 65; the card follows the member.
        Assert.Contains("Band 7–8 (NHS).", sixtyFive.Footer);
        Assert.Contains("Band 6–7 (NHS).", overSixtyFive.Footer);
        Assert.Contains("(7–8 hours)", sixtyFive.Panel);
        Assert.Contains("(6–7 hours)", overSixtyFive.Panel);
    }

    [Fact]
    public void A_card_drawing_both_marks_names_both()
    {
        var (footer, _) = MetricExplanations.For(
            "Heart Rate", Metric(baseline: 68, reference: Reference(60, 100, "AHA")), "{0:N0}");

        Assert.Equal("Dashes: their usual. Band 60–100 (AHA).", footer);
    }

    // ---- A published range that is the normal is named as the normal, and named first ----

    [Fact]
    public void A_band_that_is_the_normal_is_named_first_and_as_the_normal()
    {
        var aha = Reference(60, 100, "AHA");
        aha.IsPublishedNormal = true;

        var (footer, _) = MetricExplanations.For("Heart Rate", Metric(baseline: 68, reference: aha), "{0:N0}");

        Assert.Equal("Normal range 60–100 (AHA). Dashes: their usual.", footer);
    }

    /// <summary>
    /// The regression the band-first decision (2026-09-25) exposed: the heart-rate panel told a
    /// caregiver their own normal was "the more useful of the two" and that a rate outside the
    /// published band could be "perfectly ordinary for them" — the opposite of how the app now
    /// judges it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(68.0)]
    public void The_heart_rate_panel_says_a_rate_outside_the_band_is_worth_a_look(double? baseline)
    {
        var aha = Reference(60, 100, "AHA");
        aha.IsPublishedNormal = true;

        var (_, panel) = MetricExplanations.For(
            "Heart Rate", Metric(baseline: (decimal?)baseline, reference: aha), "{0:N0}", "Dad");

        Assert.StartsWith("The shaded band is the normal adult resting heart rate published by AHA (60–100 bpm).", panel);
        Assert.Contains("worth a look, even when it is usual for Dad", panel);
        Assert.DoesNotContain("perfectly ordinary", panel);
        Assert.DoesNotContain("more useful", panel);
    }

    [Fact]
    public void The_sleep_panel_explains_the_awake_diamond_only_when_the_window_has_one()
    {
        var withAwake = Metric(baseline: 6.5m, reference: Reference(7, 9, "NSF"));
        withAwake.Series =
        [
            new MetricPoint { Date = new DateOnly(2026, 9, 24), Value = 7m, NightStatus = NightSleepStatus.Slept },
            new MetricPoint { Date = new DateOnly(2026, 9, 25), Value = 0m, NightStatus = NightSleepStatus.Awake },
        ];

        var (_, panel) = MetricExplanations.For("Sleep", withAwake, "{0:0.#}");
        var (_, plain) = MetricExplanations.For("Sleep", Metric(baseline: 6.5m, reference: Reference(7, 9, "NSF")), "{0:0.#}");

        Assert.Contains("A diamond marks a night the watch was worn with no sleep recorded", panel);
        Assert.DoesNotContain("diamond", plain);
    }

    // ---- Every panel closes the same way ----

    [Theory]
    [InlineData("Activity")]
    [InlineData("Heart Rate")]
    [InlineData("Sleep")]
    [InlineData("Skin Temp")]
    [InlineData("Blood Oxygen")]
    [InlineData("Breathing Rate")]
    public void Every_panel_says_CardiTrack_does_not_diagnose(string name)
    {
        foreach (var metric in new[] { Metric(), Metric(baseline: 70, reference: Reference(60, 100, "AHA")) })
        {
            var (_, panel) = MetricExplanations.For(name, metric, "{0:0.#}");
            Assert.EndsWith("It doesn't diagnose.", panel);
        }
    }
}
