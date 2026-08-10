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

    /// <summary>Whether the alert carries this rule marker in its MetricValues JSON.</summary>
    internal static bool HasRule(Alert alert, string rule) =>
        alert.MetricValues?.Contains($"\"rule\":\"{rule}\"", StringComparison.Ordinal) == true;

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
    /// </summary>
    internal static bool Suppresses(Alert alert, AlertType type, string rule)
    {
        if (alert.IsResolved || alert.AlertType != type)
            return false;

        if (type == AlertType.HeartRate)
            return true;

        if (HasRule(alert, rule))
            return true;

        return type == AlertType.Inactivity
               && rule == DeviceSilenceRule
               && alert.MetricValues?.Contains("\"rule\":", StringComparison.Ordinal) != true;
    }
}
