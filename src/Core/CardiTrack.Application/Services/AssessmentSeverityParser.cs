using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Maps the model's schema-constrained severity word onto the product taxonomy
/// (docs/llm_design.md: critical/high/medium/low → red/orange/yellow/green).
/// <para>
/// The mapping is deliberately strict: a word outside the four the schema asks for yields no
/// severity at all, not a guess. The model does not get to trigger alarms by deviating from the
/// contract — an answer we cannot map is treated as no answer, and the caller's fail-safe
/// (store, log, never alert) takes over.
/// </para>
/// </summary>
public static class AssessmentSeverityParser
{
    /// <summary>
    /// The verdict from <paramref name="severityWord"/> — MedGemma's structured
    /// <c>severity</c> field — and its product-taxonomy mapping. Case is normalised only for the
    /// taxonomy match; <c>RawSeverity</c> keeps the trimmed original, since it is the audit trail
    /// read when the word does not map and exact casing may be part of what makes that worth seeing.
    /// </summary>
    public static (string? RawSeverity, AlertSeverity? Severity) Map(string? severityWord)
    {
        if (string.IsNullOrWhiteSpace(severityWord))
            return (null, null);

        var trimmed = severityWord.Trim();
        return (trimmed, trimmed.ToLowerInvariant() switch
        {
            "critical" => AlertSeverity.Red,
            "high" => AlertSeverity.Orange,
            "medium" => AlertSeverity.Yellow,
            "low" => AlertSeverity.Green,
            _ => null,
        });
    }
}
