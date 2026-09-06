using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The `rule` discriminator every automated alert producer stamps into <c>MetricValues</c>,
/// and the predicates that read it back. Two producers can share one <see cref="AlertType"/>
/// (device-silence and activity-decline are both `Inactivity`), and cooldown scope follows the
/// <em>action the family takes</em>: rules with different remedies must not suppress each
/// other, rules with the same remedy must.
/// </summary>
internal static class AlertRuleMarkers
{
    internal const string DeviceSilenceRule = "device_silence";
    internal const string RealtimeHeartRateRule = "realtime_hr";

    /// <summary>The prefix a caregiver-defined alarm's marker carries: <c>custom:{alarmId}</c>.</summary>
    internal const string CustomRulePrefix = "custom:";

    /// <summary>Whether the alert carries this rule marker in its MetricValues JSON.</summary>
    internal static bool HasRule(Alert alert, string rule) =>
        alert.MetricValues?.Contains($"\"rule\":\"{rule}\"", StringComparison.Ordinal) == true;

    /// <summary>Whether a caregiver-defined alarm raised the alert, rather than one of the built-in rules.</summary>
    internal static bool IsCustomAlarm(Alert alert) =>
        alert.MetricValues?.Contains($"\"rule\":\"{CustomRulePrefix}", StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether the alert carries this night marker — the civil day the judged night ended on,
    /// stamped by rules that judge one specific night so it dedups per night rather than per
    /// firing day (late-arriving data can put the same night in front of a rule twice).
    /// </summary>
    internal static bool HasNight(Alert alert, DateOnly night) =>
        alert.MetricValues?.Contains($"\"night\":\"{night:O}\"", StringComparison.Ordinal) == true;

    /// <summary>Whether the alert names any night at all — an alert from before night markers
    /// existed does not, and is read as the day it fired on, the same stance
    /// <see cref="Suppresses"/> takes on legacy markerless Inactivity alerts.</summary>
    internal static bool HasAnyNight(Alert alert) =>
        alert.MetricValues?.Contains("\"night\":\"", StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether the alert suppresses a new one for <paramref name="rule"/> of
    /// <paramref name="type"/>. Unresolved alerts only — resolving is what re-arms every
    /// automated producer.
    /// <para>
    /// `HeartRate` is deliberately type-scoped across producers (the AI assessor and the
    /// statistical engine): same organ, same action — "check on them" — and two simultaneous
    /// heart pages about one person is the noise cooldowns exist to prevent. Every other type
    /// is rule-scoped. An `Inactivity` alert from before rule markers existed is treated as
    /// device-silence, which was that type's only producer at the time.
    /// </para>
    /// <para>
    /// A caregiver-defined alarm is the exception to the heart rule. It re-arms through its own
    /// state row and never resolves the alert it wrote, so letting it into the type-scoped cooldown
    /// would latch the built-in and AI heart producers shut for as long as its card stays open. It
    /// suppresses its own rule only — the same alarm firing twice — and nothing else.
    /// </para>
    /// </summary>
    internal static bool Suppresses(Alert alert, AlertType type, string rule)
    {
        if (alert.IsResolved || alert.AlertType != type)
            return false;

        if (IsCustomAlarm(alert))
            return HasRule(alert, rule);

        if (type == AlertType.HeartRate)
            return true;

        if (HasRule(alert, rule))
            return true;

        return type == AlertType.Inactivity
               && rule == DeviceSilenceRule
               && alert.MetricValues?.Contains("\"rule\":", StringComparison.Ordinal) != true;
    }
}
