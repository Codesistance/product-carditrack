using System.Text.Json;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.Application.Services;

/// <summary>Which alert service call a confirmed proposal turns into.</summary>
public enum PendingAlertChangeKind
{
    /// <summary>One of CardiTrack's own rules, on or off, for this member.</summary>
    SetRule = 1,

    /// <summary>A new member-only alarm.</summary>
    CreateAlarm = 2,

    /// <summary>The member's version of an existing alarm — a retune, a rename, or the switch.</summary>
    SaveAlarm = 3,

    /// <summary>Remove the member's own alarm, or put an inherited default back.</summary>
    DeleteAlarm = 4,
}

/// <summary>
/// An alert-settings change the chat proposed and is waiting on a yes for — exactly what was
/// described to the caregiver, kept on the proposing turn so the yes applies that and nothing
/// re-derived.
/// </summary>
/// <remarks>
/// <para>
/// Honoured only while it is the most recent assistant turn and inside <see cref="Validity"/>. A
/// caregiver who reopens a week-old conversation and types "yes" must not switch off an alert
/// they had forgotten was proposed; fifteen minutes is long enough to read the proposal and
/// short enough that the reply is still about it.
/// </para>
/// <para>
/// Serialised by name — enums as strings — for the reason the turn's workflow stamp is: a stored
/// proposal must keep its meaning across a renumbering, and a caregiver's "yes" to a value that
/// silently changed meaning is the one failure this record exists to prevent. Anything that does
/// not parse is treated as no proposal at all, never coerced into one.
/// </para>
/// </remarks>
public sealed record PendingAlertChange
{
    /// <summary>How long after proposing a yes still applies it.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public required PendingAlertChangeKind Kind { get; init; }

    /// <summary>The rule for <see cref="PendingAlertChangeKind.SetRule"/>.</summary>
    public string? RuleId { get; init; }

    /// <summary>The switch for <see cref="PendingAlertChangeKind.SetRule"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>The alarm to save or delete — the account default's id or the member row's own,
    /// whichever the effective list carried, since the alarm service accepts either.</summary>
    public Guid? AlarmId { get; init; }

    /// <summary>The full alarm to write, for the create and save kinds.</summary>
    public SaveMetricAlarmRequest? Alarm { get; init; }

    /// <summary>
    /// What the alarm looked like when the change was proposed, for the save and delete kinds —
    /// <see cref="Fingerprint"/> of the row. The apply re-reads the row and refuses when it no
    /// longer matches: another caregiver may have retuned the alarm inside the window, and a yes
    /// to the old proposal must not write the old fields over their change.
    /// </summary>
    public string? AlarmFingerprint { get; init; }

    /// <summary>The fields a save would overwrite, in one comparable string.</summary>
    public static string Fingerprint(DTOs.Responses.MetricAlarmResponse row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Join("|",
            row.Name, row.Metric, row.Statistic, row.Operator, row.ThresholdKind, row.ThresholdValue,
            row.PeriodMinutes, row.EvaluationPeriods, row.DatapointsToAlarm, row.MissingDataTreatment,
            row.Severity, row.ContextGate, row.IsEnabled, row.Provenance);
    }

    /// <summary>The change as it was put to the caregiver — imperative, "switch off …".</summary>
    public required string Summary { get; init; }

    /// <summary>The same change once made — "switched off …" — so the done line reads as what
    /// happened rather than as the proposal lowercased. Null on a proposal stored before this
    /// existed; the done line then falls back to the summary.</summary>
    public string? Done { get; init; }

    public required DateTime ProposedAtUtc { get; init; }

    public bool IsCurrent(DateTime utcNow) => utcNow - ProposedAtUtc <= Validity;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The stored proposal, or null when there is none to honour: absent, unreadable,
    /// or missing the part its kind needs.</summary>
    public static PendingAlertChange? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        PendingAlertChange? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PendingAlertChange>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || !Enum.IsDefined(parsed.Kind) || string.IsNullOrWhiteSpace(parsed.Summary))
            return null;

        return parsed.Kind switch
        {
            PendingAlertChangeKind.SetRule when string.IsNullOrWhiteSpace(parsed.RuleId) => null,
            PendingAlertChangeKind.CreateAlarm when parsed.Alarm is null => null,
            PendingAlertChangeKind.SaveAlarm when parsed.Alarm is null || parsed.AlarmId is null => null,
            PendingAlertChangeKind.DeleteAlarm when parsed.AlarmId is null => null,
            _ => parsed,
        };
    }
}
