using System.Globalization;
using CardiTrack.Application.Services;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// How one chat series prints: its identity colour, the unit named beside its title, how a value
/// is spelled, and the axis ladder its figures step in.
/// </summary>
/// <remarks>
/// Keyed on the series names <c>MemberChatService.BuildCharts</c> writes, and coloured to match
/// the app's <c>ChatSeriesChart.InkFor</c> exactly — including its fallback, which leaves anything
/// past steps, resting heart rate and sleep on the primary blue. A transcript is printed next to
/// the conversation it came from, and a line that changed colour between the two would read as a
/// different reading.
/// </remarks>
internal sealed record ChatChartStyle(
    string Color,
    string Unit,
    Func<double, string> Format,
    IReadOnlyList<double>? TickSteps = null)
{
    private const string Steps = "#1884DC";      // MetricStepsInk, and Primary — the app's fallback
    private const string Heart = "#E53E3E";      // MetricHeartInk
    private const string Sleep = "#7C6FDC";      // MetricSleepInk

    /// <summary>
    /// The style for a series name, or the fallback for one this build does not know — a
    /// transcript is read back months later, and a series added after it was written must still
    /// draw.
    /// </summary>
    internal static ChatChartStyle For(string metric) => metric switch
    {
        "Steps" => new(Steps, "steps a day", Whole,
            TickSteps: [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2000, 5000, 10000]),
        "Resting heart rate" => new(Heart, "bpm", Whole, TickSteps: [1, 2, 5, 10, 20, 25, 50]),
        // Stored in minutes and read in half hours, not in fifties — the same ladder and the same
        // h/m shaping the app's chat charts use (ChatMetricFormat.Bare).
        "Sleep" or "Sleep (minutes)" => new(Sleep, "a night", Duration, TickSteps: [15, 30, 60, 120, 240]),
        "Heart rate variability" => new(Steps, "ms", Whole, TickSteps: [1, 2, 5, 10, 20, 25, 50]),
        "Breathing while asleep" => new(Steps, "breaths a minute", Fractional, TickSteps: [1, 2, 5, 10]),
        _ => new(Steps, string.Empty, Fractional),
    };

    private static string Whole(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fractional(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Duration(double minutes) =>
        ReadingFigures.SleepFigure((int)Math.Round(minutes));
}
