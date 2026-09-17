using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The readings and monitoring sections the Weekbook and the Monthbook both render, differing only
/// in which period they are an account of.
/// </summary>
/// <remarks>
/// <para>
/// The two books had a metric renderer, a monitoring section, and four number formatters each,
/// near-identical line for line — <see cref="MonthbookPrompt.MonitoringSection"/> even documented
/// itself by inheriting the Weekbook's summary. The prompt review is what made that untenable
/// rather than merely untidy: the Weekbook's brief was asking the model to subtract two numbers
/// the prompt could have subtracted for it, and fixing that in one file would have left the other
/// asking for the same arithmetic on the same readings, which is the drift a shared brief exists
/// to prevent.
/// </para>
/// <para>
/// What stays per-book is what genuinely differs: the brief, the echo list drawn from its wording,
/// the metrics each chooses to render, and the standout — a Weekbook names a day, a Monthbook
/// names a week, and those are different questions rather than the same question at two scales.
/// The Daybook shares nothing here: it is asked for completeness rather than for an average, so
/// its sections have a different shape throughout.
/// </para>
/// </remarks>
internal static class JournalPeriodSections
{
    /// <summary>
    /// The share of the member's usual within which a period's average is "about level with it"
    /// rather than a quoted amount above or below.
    /// </summary>
    internal const decimal NegligibleShare = 0.02m;

    /// <summary>
    /// One metric's period as a JSON object: the average over the days that carried it, how many
    /// those were, where that sat against the member's own usual and by how much, the published
    /// band, and the standout the calling book computes. Null when the metric was never measured.
    /// </summary>
    /// <param name="periodNoun">"week" or "month" — the period this book is an account of.</param>
    /// <param name="standoutClause">
    /// The calling book's standout, already worded, or null when it has none. A delegate rather
    /// than a shared implementation because the Weekbook's standout is a day and the Monthbook's is
    /// a week.
    /// </param>
    /// <remarks>
    /// The distance from the usual is computed here, and used to be left to the model: the briefs
    /// said "say which way it went and by how much" while the prompt gave two numbers and no
    /// difference. Both books open by saying every number is computed here, because a model asked
    /// to average seven figures will sometimes average six — the same objection applies to a
    /// subtraction, and a wrong one is undetectable by reading, since nothing else on the page
    /// contradicts it.
    /// </remarks>
    internal static JsonObject? MetricJson<T>(
        IReadOnlyList<ActivityLog> days,
        string label,
        Func<ActivityLog, T?> select,
        Func<decimal, string> format,
        // decimal rather than int: HRV and weight keep two decimal places in the baseline, and
        // widening here costs the integer baselines nothing (they convert implicitly).
        decimal? usual,
        string? band,
        string periodNoun,
        Func<IReadOnlyList<(DateOnly Day, decimal Value)>, decimal, Func<decimal, string>, string?> standoutClause)
        where T : struct, IConvertible
    {
        var measured = days
            .Select(d => (Day: d.Date, Value: select(d)))
            .Where(x => x.Value.HasValue)
            .Select(x => (x.Day, Value: Convert.ToDecimal(x.Value!.Value, CultureInfo.InvariantCulture)))
            .ToList();

        if (measured.Count == 0)
            return null;

        var average = measured.Average(m => m.Value);
        var obj = new JsonObject
        {
            ["label"] = label,
            ["average"] = format(average),
            ["measured_days"] = measured.Count,
            ["period"] = periodNoun,
        };

        if (usual is { } usualValue)
        {
            obj["usual"] = format(usualValue);

            // A difference too small to mean anything is said as "about level" rather than by
            // the amount: the brief tells the model to quote the amount given, and given "24
            // steps above" it does — a Monthbook read as a recital of one-minute and half-a-beat
            // differences, each true and none worth a sentence. Two per cent of the usual is
            // eight or nine minutes of a seven-hour night, a beat and a half of a resting heart
            // rate, and under a hundred steps of a day's walking.
            var difference = average - usualValue;
            var negligible = Math.Abs(usualValue) * NegligibleShare;
            obj["vs_usual"] = difference switch
            {
                0 => $"the {periodNoun} sat level with it",
                _ when Math.Abs(difference) <= negligible => $"the {periodNoun} sat about level with it",
                > 0 => $"the {periodNoun} sat {format(difference)} above it",
                _ => $"the {periodNoun} sat {format(-difference)} below it",
            };
        }

        if (band is not null)
            obj["published_band"] = band;

        if (standoutClause(measured, average, format) is { } standout)
            obj["standout"] = standout;

        return obj;
    }

    /// <summary>
    /// The period's readings as a fenced JSON array of metric objects, under the same
    /// <c>--- label ---</c> heading the rest of the prompt uses. Empty when nothing was measured.
    /// </summary>
    internal static string ReadingsJson(string heading, IReadOnlyList<JsonObject> metrics)
    {
        if (metrics.Count == 0)
            return string.Empty;

        var array = new JsonArray();
        foreach (var metric in metrics)
            array.Add(metric);

        var root = new JsonObject { ["metrics"] = array };
        return "--- " + heading + " ---\n"
            + MedicalPromptBlocks.JsonFence(MedicalPromptBlocks.WearableJsonString(root));
    }

    /// <summary>
    /// The period's monitoring: what fired and what the assessor called, as counts. Counts, never
    /// scores — the standing no-risk-scores decision (release matrix).
    /// </summary>
    /// <remarks>
    /// Only <see cref="AlertSeverity.Yellow"/> and above count as observed, the same bar the
    /// Daybook applies. <c>RealtimeAssessments</c> holds a row for every assessed window, including
    /// ordinary ones and ones whose severity would not parse — counting those would report a calm
    /// period as a heavily monitored one, which is the mirror image of the mistake the coverage
    /// guard exists to prevent: there, silence must not read as health; here, routine must not read
    /// as concern.
    /// </remarks>
    internal static string MonitoringSection(
        string label,
        string periodNoun,
        IReadOnlyList<Alert> alerts,
        IReadOnlyList<RealtimeAssessment> assessments)
    {
        var notable = assessments
            .Where(a => a.Severity is { } severity && severity >= AlertSeverity.Yellow)
            .ToList();

        if (alerts.Count == 0 && notable.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- ").Append(label).AppendLine(" ---");

        if (alerts.Count > 0)
        {
            var unresolved = alerts.Count(a => !a.IsResolved);
            sb.Append(alerts.Count)
              .Append(alerts.Count == 1 ? " alert was raised" : " alerts were raised")
              .Append(" during the ")
              .Append(periodNoun);

            if (unresolved > 0)
                sb.Append(", ").Append(unresolved).Append(" still unresolved at the end of it");

            sb.AppendLine(".");

            // Named by kind so the account can say what the period's monitoring was about, without
            // handing the model a stack of alert bodies to paraphrase into a diagnosis.
            foreach (var group in alerts.GroupBy(a => a.Severity).OrderByDescending(g => g.Key))
            {
                sb.Append("- ")
                  .Append(group.Count())
                  .Append(' ')
                  .Append(group.Key.ToString().ToLowerInvariant())
                  .AppendLine(group.Count() == 1 ? " alert" : " alerts");
            }
        }

        if (notable.Count > 0)
        {
            sb.Append(notable.Count)
              .Append(notable.Count == 1
                  ? " hour was observed as worth noting"
                  : " hours were observed as worth noting")
              .Append(" by the real-time monitoring during the ")
              .Append(periodNoun)
              .AppendLine(".");
        }

        return sb.ToString().TrimEnd();
    }

    internal static string Hours(int minutes) =>
        $"{minutes / 60}h {minutes % 60:D2}m";

    internal static string Band(decimal low, decimal high, string unit, string source) =>
        $"The published range is {Trim(low)}-{Trim(high)} {unit} ({source})";

    private static string Trim(decimal value) =>
        (value == Math.Truncate(value) ? Math.Truncate(value) : Math.Round(value, 1))
            .ToString(CultureInfo.InvariantCulture);
}
