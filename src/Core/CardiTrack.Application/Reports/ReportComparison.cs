using System.Globalization;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Reports;

/// <summary>One metric over the exported period, against this member's own usual and the published band.</summary>
/// <param name="Metric">Plain words, as the document prints them.</param>
/// <param name="Unit">What the figures are in.</param>
/// <param name="PeriodAverage">The mean over the days the export covers that carried a reading.</param>
/// <param name="Usual">The member's own learned figure, or null where none was learned.</param>
/// <param name="ChangePercent">Signed whole percent against <paramref name="Usual"/>, null without one.</param>
/// <param name="BandLow">Published band floor, null for a metric no body publishes one for.</param>
/// <param name="BandHigh">Published band ceiling.</param>
/// <param name="BandSource">Who publishes the band — printed beside it, never implied.</param>
/// <param name="MeasuredDays">How many days in the period carried a reading for this metric.</param>
public sealed record ReportComparisonRow(
    string Metric,
    string Unit,
    decimal PeriodAverage,
    decimal? Usual,
    decimal? ChangePercent,
    decimal? BandLow,
    decimal? BandHigh,
    string? BandSource,
    int MeasuredDays);

/// <summary>
/// The deterministic "how this compares" block every export format shares.
/// </summary>
/// <remarks>
/// <para>
/// Reports were the weakest surface in the product for this: the PDF's narrative saw a column of
/// raw day figures and a list of alert titles, with no baseline and no published band anywhere in
/// the prompt. So a document a caregiver might hand to a clinician could report a fortnight of
/// resting heart rates without once saying what this person's own resting heart rate usually is.
/// </para>
/// <para>
/// Computed here rather than in each renderer, and computed rather than narrated: the PDF prints
/// these rows itself, so the comparison survives a model call that fails, times out, or comes back
/// hedged. The narrative is given the same rows as grounding, which is the arrangement the journal
/// books already use — <c>JournalPeriodSections.MetricJson</c> tells the model never to work a
/// comparison out for itself, because one already has been.
/// </para>
/// </remarks>
public static class ReportComparison
{
    /// <summary>
    /// The rows for one member over the exported period. Empty when they have no baseline yet or
    /// nothing in the period was measured — an export for a member still being learned carries no
    /// comparison section rather than a section of dashes.
    /// </summary>
    public static IReadOnlyList<ReportComparisonRow> For(ReportMemberData member, int ageYears)
    {
        if (member.Baseline is not { } baseline)
            return [];

        // Ingestion upserts per (DeviceConnection, Date), so a member wearing two devices has two
        // rows for the same day. Collapsed to the most recently written row per date — the rule
        // BaselineCalculator applies, so the period average and the usual it is compared against
        // are drawn the same way. Without it a two-device member's export averaged every row, and
        // MeasuredDays reported a fortnight as twenty-eight days.
        var days = member.ActivityLogs
            .GroupBy(log => log.Date)
            .Select(g => g.OrderByDescending(log => log.UpdatedDate ?? log.CreatedDate).First())
            .ToList();

        var sleepBand = HealthReferenceRanges.Sleep(ageYears);
        var heartBand = HealthReferenceRanges.RestingHeartRate;
        var oxygenBand = HealthReferenceRanges.SpO2;
        var breathingBand = HealthReferenceRanges.BreathingRate;

        var rows = new List<ReportComparisonRow?>
        {
            Row("Steps", "a day", days, log => log.Steps, baseline.AvgSteps, null, null, null),
            Row("Resting heart rate", "bpm", days, log => log.RestingHeartRate,
                baseline.AvgRestingHeartRate, heartBand.Low, heartBand.High, heartBand.Source),
            // Hours, not minutes: a report is read by people, and 432 is not a night's sleep to
            // anyone but a database.
            Row("Sleep", "hours a night", days, log => Hours(log.SleepMinutes),
                Hours(baseline.AvgSleepMinutes), sleepBand.Low, sleepBand.High, sleepBand.Source),
            Row("Active minutes", "a day", days, log => log.ActiveMinutes,
                baseline.AvgActiveMinutes, null, null, null),
            Row("Overnight heart rate variability", "ms", days, log => log.HeartRateVariabilityMs,
                baseline.AvgHeartRateVariabilityMs, null, null, null),
            Row("Breathing rate asleep", "breaths a minute", days,
                log => log.OvernightBreathingRate, baseline.AvgOvernightBreathingRate,
                breathingBand.Low, breathingBand.High, breathingBand.Source),
            Row("Blood oxygen", "%", days, log => log.SpO2Average, null,
                oxygenBand.Low, oxygenBand.High, oxygenBand.Source),
        };

        return rows.OfType<ReportComparisonRow>().ToList();
    }

    /// <summary>
    /// The block as prompt text — the same rows the document prints, so the narrative cannot
    /// describe a comparison the table beside it does not show.
    /// </summary>
    public static string Render(IReadOnlyList<ReportComparisonRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var lines = new List<string>
        {
            "### How this period compares (already computed — state these as given, never work "
            + "out a comparison yourself)",
        };

        foreach (var row in rows)
        {
            var parts = new List<string>
            {
                $"averaged {Figure(row.PeriodAverage)} {row.Unit} over {row.MeasuredDays} measured day(s)",
            };

            if (row.Usual is { } usual)
            {
                parts.Add(row.ChangePercent is { } change && change != 0
                    ? $"their own usual is {Figure(usual)}, so {Math.Abs(change):0}% "
                      + (change < 0 ? "below" : "above") + " it"
                    : $"their own usual is {Figure(usual)}, about level with it");
            }

            if (row is { BandLow: { } low, BandHigh: { } high, BandSource: { } source })
                parts.Add($"the published range is {Figure(low)}-{Figure(high)} ({source})");

            lines.Add($"- {row.Metric}: {string.Join("; ", parts)}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ReportComparisonRow? Row(
        string metric,
        string unit,
        IReadOnlyList<ActivityLog> days,
        Func<ActivityLog, decimal?> read,
        decimal? usual,
        decimal? bandLow,
        decimal? bandHigh,
        string? bandSource)
    {
        var measured = days.Select(read).OfType<decimal>().ToList();
        if (measured.Count == 0)
            return null;

        var average = Math.Round(measured.Average(), 1);

        return new ReportComparisonRow(
            metric,
            unit,
            average,
            usual is null ? null : Math.Round(usual.Value, 1),
            usual is > 0 ? Math.Round((average - usual.Value) / usual.Value * 100m, 0) : null,
            bandLow,
            bandHigh,
            bandSource,
            measured.Count);
    }

    private static decimal? Hours(decimal? minutes) =>
        minutes is { } m ? Math.Round(m / 60m, 1) : null;

    /// <summary>Trailing zeros dropped, so 7.0 hours prints as 7 and 6.4 stays 6.4.</summary>
    private static string Figure(decimal value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);
}
