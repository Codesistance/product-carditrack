using System.Globalization;
using System.Text;
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
    /// One metric's period: the average over the days that carried it, how many those were, where
    /// that sat against the member's own usual and by how much, the published band, and the
    /// standout the calling book computes. Returns 1 when a line was written, 0 when the metric was
    /// never measured.
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
    internal static int AppendMetric<T>(
        StringBuilder sb,
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
            return 0;

        var average = measured.Average(m => m.Value);

        sb.Append(label)
          .Append(": ")
          .Append(format(average))
          .Append(" on average, measured on ")
          .Append(measured.Count)
          .Append(measured.Count == 1 ? " day of the " : " days of the ")
          .Append(periodNoun);

        if (usual is { } usualValue)
        {
            sb.Append(". Their usual is ").Append(format(usualValue));

            var difference = average - usualValue;
            sb.Append(difference switch
            {
                > 0 => $", and the {periodNoun} sat {format(difference)} above it",
                < 0 => $", and the {periodNoun} sat {format(-difference)} below it",
                _ => $", and the {periodNoun} sat level with it",
            });
        }

        if (band is not null)
            sb.Append(". ").Append(band);

        if (standoutClause(measured, average, format) is { } standout)
            sb.Append(". ").Append(standout);

        sb.AppendLine(".");
        return 1;
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
