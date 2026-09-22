using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The two measured rules and the episode statistics behind them. Measured rules take no
/// baseline, so what is pinned here is the other discipline: null means "we could not see this
/// wearer's rhythm data" and zero means "we looked and the device raised nothing", and the two
/// must never collapse into each other.
/// </summary>
public class RhythmRulesTests
{
    private static ActivityLog Log(
        int? notifications = null,
        int? afibReadings = null,
        int? ecgReadings = null,
        int dayOffset = 0) => new()
    {
        Date = new DateOnly(2026, 9, 22).AddDays(dayOffset),
        IrregularRhythmNotifications = notifications,
        EcgAtrialFibrillationReadings = afibReadings,
        EcgReadings = ecgReadings,
    };

    // ── irregular_rhythm ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void IrregularRhythm_fires_on_a_notification()
    {
        var finding = StatisticalAlertRules.IrregularRhythm(Log(notifications: 1), null);

        Assert.NotNull(finding);
        Assert.Equal(StatisticalAlertRules.IrregularRhythmRule, finding.Rule);
        Assert.Equal(AlertType.Rhythm, finding.Type);
        Assert.Equal(new DateOnly(2026, 9, 22), finding.NightOf);
    }

    [Fact]
    public void IrregularRhythm_is_silent_on_a_measured_zero()
    {
        Assert.Null(StatisticalAlertRules.IrregularRhythm(Log(notifications: 0), null));
    }

    [Fact]
    public void IrregularRhythm_is_silent_when_the_scope_was_never_granted()
    {
        // Null, not zero: the read could not be made at all.
        Assert.Null(StatisticalAlertRules.IrregularRhythm(Log(notifications: null), null));
    }

    [Fact]
    public void IrregularRhythm_falls_back_to_yesterday_when_today_is_quiet()
    {
        // A notification raised late in the evening lands on that day's row, and the member's
        // calendar may have rolled over before the pass sees it.
        var finding = StatisticalAlertRules.IrregularRhythm(
            Log(notifications: 0), Log(notifications: 2, dayOffset: -1));

        Assert.NotNull(finding);
        Assert.Equal(new DateOnly(2026, 9, 21), finding.NightOf);
    }

    [Fact]
    public void IrregularRhythm_prefers_today_when_both_days_carry_one()
    {
        var finding = StatisticalAlertRules.IrregularRhythm(
            Log(notifications: 1), Log(notifications: 3, dayOffset: -1));

        Assert.NotNull(finding);
        Assert.Equal(new DateOnly(2026, 9, 22), finding.NightOf);
    }

    [Fact]
    public void IrregularRhythm_names_the_device_as_the_source()
    {
        var finding = StatisticalAlertRules.IrregularRhythm(Log(notifications: 1), null);

        // The model writes the family's words from this observation, and the distinction between
        // "the watch found this" and "CardiTrack found this" is the whole register boundary.
        Assert.NotNull(finding);
        Assert.Contains("own device", finding.Observation, StringComparison.Ordinal);
        Assert.Contains("not a CardiTrack one", finding.Observation, StringComparison.Ordinal);
    }

    // ── ecg_afib ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EcgAtrialFibrillation_fires_on_a_classified_reading()
    {
        var finding = StatisticalAlertRules.EcgAtrialFibrillation(
            Log(afibReadings: 1, ecgReadings: 3), null);

        Assert.NotNull(finding);
        Assert.Equal(StatisticalAlertRules.EcgAtrialFibrillationRule, finding.Rule);
        Assert.Equal(AlertType.Rhythm, finding.Type);
    }

    [Fact]
    public void EcgAtrialFibrillation_is_silent_when_readings_were_taken_but_none_were_afib()
    {
        // Inconclusive and unreadable classifications never reach this column, so a day of three
        // ECGs and no AFib is a day the device declined to judge or judged normal.
        Assert.Null(StatisticalAlertRules.EcgAtrialFibrillation(
            Log(afibReadings: 0, ecgReadings: 3), null));
    }

    [Fact]
    public void EcgAtrialFibrillation_is_silent_when_the_scope_was_never_granted()
    {
        Assert.Null(StatisticalAlertRules.EcgAtrialFibrillation(Log(afibReadings: null), null));
    }

    [Fact]
    public void EcgAtrialFibrillation_says_a_trace_exists_to_show_a_clinician()
    {
        var finding = StatisticalAlertRules.EcgAtrialFibrillation(
            Log(afibReadings: 1, ecgReadings: 1), null);

        // The difference between this and a notification is that there is a recording behind it.
        Assert.NotNull(finding);
        Assert.Contains("clinician", finding.Observation, StringComparison.Ordinal);
    }

    // ── episode statistics ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summarise_returns_zeroes_and_no_rmssd_for_a_window_without_beats()
    {
        // A notification whose beats the provider did not serve is still a notification; readers
        // tell that case apart by BeatCount, not by the statistics.
        var (mean, min, max, rmssd) = RhythmEpisodeStatistics.Summarise([]);

        Assert.Equal(0, mean);
        Assert.Equal(0, min);
        Assert.Equal(0, max);
        Assert.Null(rmssd);
    }

    [Fact]
    public void Summarise_has_no_rmssd_for_a_single_beat()
    {
        // One beat has no successive difference — distinct from a zero RMSSD, which is real.
        var (mean, min, max, rmssd) = RhythmEpisodeStatistics.Summarise([800]);

        Assert.Equal(800, mean);
        Assert.Equal(800, min);
        Assert.Equal(800, max);
        Assert.Null(rmssd);
    }

    [Fact]
    public void Summarise_reports_zero_rmssd_for_a_perfectly_regular_window()
    {
        var (_, _, _, rmssd) = RhythmEpisodeStatistics.Summarise([800, 800, 800, 800]);

        Assert.Equal(0, rmssd);
    }

    [Fact]
    public void Summarise_computes_rmssd_over_successive_differences()
    {
        // Differences: +100, -100, +100 → sqrt((10000 + 10000 + 10000) / 3) = 100.
        var (mean, min, max, rmssd) = RhythmEpisodeStatistics.Summarise([700, 800, 700, 800]);

        Assert.Equal(750, mean);
        Assert.Equal(700, min);
        Assert.Equal(800, max);
        Assert.Equal(100, rmssd);
    }

    [Fact]
    public void Summarise_divides_by_the_number_of_differences_not_of_beats()
    {
        // The easy mistake: dividing by Count rather than Count - 1. With three beats and two
        // differences of 100 each, the correct answer is 100 and the wrong one is ~82.
        var (_, _, _, rmssd) = RhythmEpisodeStatistics.Summarise([700, 800, 700]);

        Assert.Equal(100, rmssd);
    }
}
