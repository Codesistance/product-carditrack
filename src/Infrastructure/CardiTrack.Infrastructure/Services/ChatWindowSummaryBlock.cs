using System.Globalization;
using System.Text.Json.Nodes;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The window's per-reading summary as the clinical read is shown it: every average, spread and
/// comparison a question about a stretch of days needs, computed in code by
/// <see cref="ReadingWindowSummaries"/> and written in the units the reply will quote.
/// </summary>
/// <remarks>
/// <para>
/// The daily rows beside it stay, because a question about one day is answered from its row. What
/// this adds is the arithmetic the rows used to leave to the model — which, handed seven nights of
/// sleep on 2026-09-25, told a caregiver the week averaged 2h 22m against nights that ran 4h 18m
/// to 7h. The comparisons are subtracted here for the same reason
/// <c>JournalPeriodSections.MetricJson</c> subtracts them: a wrong subtraction is undetectable by
/// reading, because nothing else on the page contradicts it.
/// </para>
/// <para>
/// A reading that did not reach enough days carries no average at all, and says why — the
/// instruction under the block tells the model to give its daily readings instead. Leaving the
/// field out would let the model supply one.
/// </para>
/// </remarks>
internal static class ChatWindowSummaryBlock
{
    /// <summary>
    /// The block, or null when the window has nothing to summarise — a single day, or no reading
    /// on any day of it and none asked about.
    /// </summary>
    /// <param name="askedMetrics">Readings to write even when no day carried them — see
    /// <see cref="ReadingWindowSummaries.For"/>.</param>
    internal static string? Render(
        FetchedMemberData data, DateOnly today, int? ageYears, IReadOnlyList<ChartMetricKind>? askedMetrics)
    {
        if (data.RecentActivityWindow is not { } window)
            return null;

        var summaries = ReadingWindowSummaries.For(data.RecentActivity, window, today, data.Baseline, ageYears, askedMetrics);
        if (summaries.Count == 0)
            return null;

        var array = new JsonArray();
        foreach (var summary in summaries)
            array.Add(Metric(summary, window.From));

        return "--- Window summary (computed in code from the window's daily readings) ---\n"
            + MedicalPromptBlocks.JsonFence(MedicalPromptBlocks.WearableJsonString(array))
            + "\nEvery average, spread and comparison across the window is computed above. Quote these"
            + " figures for any average, total or comparison over the window — never add up or average"
            + " the daily readings yourself. Where a reading has no average because too few days carried"
            + " it, do not give one: answer with its daily readings instead.";
    }

    private static JsonObject Metric(ReadingWindowSummary summary, DateOnly from)
    {
        var unit = ReadingWindowSummaries.IsOvernight(summary.Metric) ? "nights" : "days";
        var to = from.AddDays(summary.DaysConsidered - 1);
        var obj = new JsonObject
        {
            ["reading"] = ReadingWindowSummaries.Name(summary.Metric),
            ["covers"] = $"{Day(from)} to {Day(to)}",
            [$"{unit}_with_reading"] = $"{summary.Readings.Count} of {summary.DaysConsidered}",
        };

        if (summary.Average is not { } average)
        {
            obj["average"] = null;
            obj["note"] = $"only {summary.Readings.Count} of {summary.DaysConsidered} {unit} carried a reading,"
                + $" and {summary.RequiredDays} are needed to average them — give the daily readings instead";
            return obj;
        }

        obj["average"] = Figure(summary.Metric, average);
        if (summary.Lowest is { } lowest)
            obj["lowest"] = $"{Day(lowest.Day)}: {ReadingWindowSummaries.DayFigure(summary.Metric, lowest.Value)}";
        if (summary.Highest is { } highest)
            obj["highest"] = $"{Day(highest.Day)}: {ReadingWindowSummaries.DayFigure(summary.Metric, highest.Value)}";

        if (summary.Band is { } band)
        {
            obj["published_range"] = $"{Figure(summary.Metric, band.Low)} to {Figure(summary.Metric, band.High)} ({band.Source})";
            obj["average_vs_published_range"] = average < band.Low
                ? $"{Figure(summary.Metric, band.Low - average)} below its {Figure(summary.Metric, band.Low)} floor"
                : average > band.High
                    ? $"{Figure(summary.Metric, average - band.High)} above its {Figure(summary.Metric, band.High)} ceiling"
                    : "within it";
        }

        if (summary.Usual is { } usual)
        {
            obj["usual_for_this_member"] = Figure(summary.Metric, usual);

            // The journal books' "about level" rule, for the same reason: given a four-minute or
            // half-a-beat difference to quote, a model quotes it, and a reply made of those says
            // nothing a caregiver can use.
            var difference = average - usual;
            obj["average_vs_usual"] = Math.Abs(difference) <= Math.Abs(usual) * JournalPeriodSections.NegligibleShare
                ? "about level with it"
                : difference > 0
                    ? $"{Figure(summary.Metric, difference)} above it"
                    : $"{Figure(summary.Metric, -difference)} below it";
        }

        return obj;
    }

    private static string Figure(ChartMetricKind metric, decimal value) =>
        ReadingWindowSummaries.Figure(metric, value);

    private static string Day(DateOnly date) => date.ToString("MMM d", CultureInfo.InvariantCulture);
}
