using System.Text.RegularExpressions;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Extracts the severity verdict from the model's free-text assessment and maps it onto the
/// product taxonomy (docs/llm_design.md: critical/high/medium/low → red/orange/yellow/green).
/// <para>
/// The parser is deliberately strict: only an explicit <c>Severity: &lt;word&gt;</c> line
/// counts, and anything else yields no severity at all. The model does not get to trigger
/// alarms by mumbling — an answer we cannot parse is treated as no answer, and the caller's
/// fail-safe (store, log, never alert) takes over.
/// </para>
/// </summary>
public static partial class AssessmentSeverityParser
{
    /// <summary>
    /// The verdict pulled out of <paramref name="modelOutput"/>: the literal word the model
    /// used (null when no severity line was found) and its product-taxonomy mapping.
    /// </summary>
    public static (string? RawSeverity, AlertSeverity? Severity) Parse(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return (null, null);

        // The last match wins: the prompt asks for the verdict as the closing line, and a model
        // that first *discusses* severity levels and then concludes should be read by its
        // conclusion, not its deliberation.
        Match? last = null;
        foreach (Match match in SeverityLine().Matches(modelOutput))
            last = match;

        if (last is null)
            return (null, null);

        var word = last.Groups["word"].Value.ToLowerInvariant();
        return (word, word switch
        {
            "critical" => AlertSeverity.Red,
            "high" => AlertSeverity.Orange,
            "medium" => AlertSeverity.Yellow,
            "low" => AlertSeverity.Green,
            _ => null,
        });
    }

    /// <summary>
    /// <c>Severity: &lt;word&gt;</c> at a line start, tolerating markdown emphasis around the
    /// label and the word — models bold their conclusions — but never a severity word loose in
    /// prose.
    /// </summary>
    [GeneratedRegex(@"^\s*\**\s*Severity\s*\**\s*:\s*\**\s*(?<word>critical|high|medium|low)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SeverityLine();
}
