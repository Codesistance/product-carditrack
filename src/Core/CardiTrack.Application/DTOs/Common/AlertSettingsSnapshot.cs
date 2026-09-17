using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// What is currently watching one member, as the settings rung reads it before planning a change:
/// CardiTrack's own rules with their effective on/off state, and the member's effective alarms
/// under the labels the planning prompt offers for them.
/// </summary>
/// <remarks>
/// Built once per turn from the same services the settings pages read, so chat and the pages
/// cannot disagree about what is on. The alarm labels are positional — <c>alarm-1</c> is the first
/// row in the list the prompt rendered — and they exist so the model can point at a row without
/// ever seeing or returning its id.
/// </remarks>
public sealed record AlertSettingsSnapshot
{
    /// <summary>Every catalogue rule, implemented or not, with effective enablement.</summary>
    public required IReadOnlyList<AlertRuleSettingResponse> Rules { get; init; }

    /// <summary>The member's effective alarms — inherited, overridden and their own.</summary>
    public required IReadOnlyList<AlarmSnapshotEntry> Alarms { get; init; }

    /// <summary>The label the prompt gives the alarm at <paramref name="index"/>.</summary>
    public static string LabelFor(int index) => $"alarm-{index + 1}";

    public AlertRuleSettingResponse? FindRule(string? ruleId) =>
        string.IsNullOrWhiteSpace(ruleId)
            ? null
            : Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId.Trim(), StringComparison.Ordinal));

    public AlarmSnapshotEntry? FindAlarm(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? null
            : Alarms.FirstOrDefault(a => string.Equals(a.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>How many of the member's effective alarms are switched on — what the ceiling counts.</summary>
    public int EnabledAlarmCount => Alarms.Count(a => a.Row.IsEnabled);
}

/// <param name="Label">The prompt's positional label — what the model answers with.</param>
/// <param name="RedactedName">The caregiver's own name for the alarm with the member's name
/// swapped for the placeholder, since the name is free text that reaches the Rewrite slot.</param>
/// <param name="Row">The alarm as the settings page sees it — the one the reply describes and the
/// one an edit starts from.</param>
public sealed record AlarmSnapshotEntry(string Label, string RedactedName, MetricAlarmResponse Row);
