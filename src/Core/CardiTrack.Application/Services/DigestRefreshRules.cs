using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// When a family summary may regenerate inside the 20-minute floor. The floor exists so a
/// continuously-uploading device does not buy an inference every pass for wording that barely
/// moves; these are the cases where the wording <em>should</em> move, and making a caregiver
/// wait the floor out would be the floor working against the thing it protects.
/// <para>
/// Pure functions from already-loaded rows, deliberately free of I/O so every threshold is
/// unit-testable to its boundary — the same stance as <see cref="StatisticalAlertRules"/>.
/// The numbers are this product's existing yardsticks, not a second opinion: the sample-jump
/// score is the one the real-time prompt already tells the model is ordinary variation, and
/// daily divergence reuses the R1 statistical rules so the summary and the alerts cannot
/// disagree about what "off the usual" means.
/// </para>
/// </summary>
public static class DigestRefreshRules
{
    /// <summary>
    /// SSA deviation scores at or above this are a jump, not ordinary jitter. The real-time
    /// assessment prompt states the same bound ("scores under 3 are ordinary variation"), so a
    /// summary that waited out the floor after a score of 3 would be waiting on a change the
    /// assessor had already called out.
    /// </summary>
    public const double SampleJumpScore = 3.0;

    /// <summary>
    /// Absolute SpO₂ drop, percentage points, that counts as a jump from yesterday. Percent-of-
    /// baseline does not work here: 30% of 97 is not a reading a person still has.
    /// </summary>
    public const decimal SpO2JumpPoints = 3m;

    /// <summary>
    /// True when the latest real-time window is new since the last summary <em>and</em> either
    /// the model called it a problem (yellow or above) or the SSA score itself is a jump. A
    /// green window with an ordinary score is more of the same readings — that is what the
    /// floor is for. A yellow window that has not (yet) become an alert is exactly the gap
    /// the alert-only waiver used to leave: medium observations rode the ordinary cycle, and
    /// a caregiver watching someone in a bad way waited twenty minutes to read it.
    /// </summary>
    public static bool SamplesIndicateAProblem(RealtimeAssessment? latest, DateTime previousSummaryAt)
    {
        if (latest is null || latest.GeneratedAtUtc <= previousSummaryAt)
            return false;

        if (latest.Severity >= AlertSeverity.Yellow)
            return true;

        return latest.HrDeviationScore >= SampleJumpScore;
    }

    /// <summary>
    /// True when today's or yesterday's daily readings would fire an R1 statistical rule against
    /// the established 30-day baseline. Today's resting heart rate is judged with the same
    /// elevated-HR rule that otherwise waits until it becomes "yesterday" — a spike in progress
    /// should not wait until tomorrow's evaluation to rewrite the summary.
    /// </summary>
    /// <remarks>
    /// Steps-decline stays on yesterday: today's total is in progress, and comparing a morning's
    /// few thousand to a 6,000-step usual would waive the floor every morning of every member.
    /// Sleep lives on today's row (the night that just ended), so last night is fair game.
    /// </remarks>
    public static bool ReadingsDivergeFromBaseline(
        PatternBaseline? baseline, ActivityLog? today, ActivityLog? yesterday)
    {
        if (baseline is null)
            return false;

        return StatisticalAlertRules.ActivityDecline(baseline, yesterday) is not null
            // The trigger, not the graded candidate: this asks whether the readings diverge, and
            // the severity that grading would put on the divergence is not part of the question.
            || StatisticalAlertRules.SleepDepartsFromBaseline(baseline, today)
            || StatisticalAlertRules.ElevatedHeartRate(baseline, yesterday) is not null
            || StatisticalAlertRules.ElevatedHeartRate(baseline, today) is not null;
    }

    /// <summary>
    /// True when today's complete-enough readings jumped from yesterday's by more than
    /// <see cref="StatisticalAlertRules.DeviationFraction"/> (or <see cref="SpO2JumpPoints"/>
    /// for oxygen). Steps are ignored: today's total only rises, so a morning-vs-yesterday
    /// comparison is a false decline, not a jump.
    /// </summary>
    public static bool ReadingsJumpedFromPrevious(ActivityLog? today, ActivityLog? yesterday)
    {
        if (today is null || yesterday is null)
            return false;

        return Jumped(today.SleepMinutes, yesterday.SleepMinutes)
            || Jumped(today.RestingHeartRate, yesterday.RestingHeartRate)
            || Jumped(today.AvgHeartRate, yesterday.AvgHeartRate)
            || SpO2Jumped(today.SpO2Average, yesterday.SpO2Average);
    }

    private static bool Jumped(int? current, int? previous)
    {
        if (current is not { } now || previous is not > 0)
            return false;

        return Math.Abs(now - previous.Value) > previous.Value * StatisticalAlertRules.DeviationFraction;
    }

    private static bool SpO2Jumped(decimal? current, decimal? previous)
    {
        if (current is not { } now || previous is not { } prior)
            return false;

        return Math.Abs(now - prior) >= SpO2JumpPoints;
    }
}
