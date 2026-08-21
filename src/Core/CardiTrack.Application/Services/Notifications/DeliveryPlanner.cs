using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Decides how one delivery to one recipient is scheduled, keyed, and whether it may carry the
/// <c>critical</c> APNs flag — pure over <see cref="DeliveryPlanningContext"/>, no I/O, no clock
/// call, mirroring <see cref="NudgeContext"/>'s <c>TimeProvider</c>-as-field shape so it stays
/// table-testable with no host (notification_engine.md §6.2, §6.3, §7.2).
/// </summary>
/// <remarks>
/// Operates per (recipient, content) pair. Fan-out across a CardiMember's caregivers — Health
/// deliveries reach every caregiver with <c>ReceiveAlerts</c>, per §10.3 — is the caller's job
/// (<c>DispatchService</c>); this type only ever plans one row at a time, the same separation
/// <c>NudgeRuleCatalogue</c> keeps between "does this gap exist" and "who gets nagged".
/// </remarks>
public static class DeliveryPlanner
{
    /// <summary>
    /// A red or orange Health delivery may request the Critical Alerts entitlement flag. Server-
    /// side only, from this fixed allowlist — never derived from client or producer input, the
    /// control that keeps a compromised enqueue path from waking every user at 3am (§7.2, Critical
    /// Alerts abuse surface). Public so the send-time code that actually sets the APNs
    /// <c>interruption-level</c>/<c>critical</c> fields (<c>FcmNotificationChannel</c>) evaluates
    /// the identical rule against the row's stored <see cref="Domain.Entities.NotificationDelivery.Category"/>/
    /// <see cref="Domain.Entities.NotificationDelivery.Severity"/> rather than re-deriving a
    /// second, potentially-drifting copy of this allowlist.
    /// </summary>
    public static bool AllowsCritical(DeliveryCategory category, AlertSeverity? severity) =>
        category switch
        {
            DeliveryCategory.Safety => true,
            DeliveryCategory.Health => severity == AlertSeverity.Red,
            _ => false
        };

    public static DeliveryPlan Plan(DeliveryPlanningContext context)
    {
        var pushes = context.Category switch
        {
            // Safety always pushes immediately and overrides quiet hours.
            DeliveryCategory.Safety => true,
            // Health: red and orange push; yellow/green are in-app + digest only.
            DeliveryCategory.Health => context.Severity is AlertSeverity.Red or AlertSeverity.Orange,
            // Nudges never push except the two safety-class rules, which arrive as
            // DeliveryCategory.Safety already — a Nudge-category row is always in-app.
            DeliveryCategory.Nudge => false,
            // A question is an invitation, not an anomaly, so it never overrides quiet hours (see
            // overridesQuietHours below) — but it must reach the family, so unlike a Nudge it does
            // push.
            DeliveryCategory.Questionnaire => true,
            _ => false
        };

        var channel = pushes ? DeliveryChannel.Push : DeliveryChannel.InApp;

        var overridesQuietHours = context.Category == DeliveryCategory.Safety
            || (context.Category == DeliveryCategory.Health && context.Severity == AlertSeverity.Red);

        DateTime? scheduledFor = null;
        if (pushes && !overridesQuietHours && context.IsWithinQuietHours)
        {
            // Orange Health defers to the end of quiet hours; nothing else reaches this branch,
            // since Safety and red Health already overrode above.
            scheduledFor = context.QuietHoursEndUtc;
        }

        var critical = pushes && AllowsCritical(context.Category, context.Severity);

        var ttl = context.Category == DeliveryCategory.Safety || context.Severity == AlertSeverity.Red
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(5);

        return new DeliveryPlan(
            Channel: channel,
            DedupKey: context.DedupKey,
            CollapseKey: context.CollapseKey,
            ScheduledFor: scheduledFor,
            ExpiresAt: (scheduledFor ?? context.UtcNow) + ttl,
            AllowCritical: critical,
            Escalates: context.Category == DeliveryCategory.Safety
                || (context.Category == DeliveryCategory.Health && context.Severity == AlertSeverity.Red));
    }
}

public sealed record DeliveryPlanningContext
{
    public required DateTime UtcNow { get; init; }
    public required DeliveryCategory Category { get; init; }

    /// <summary>Null for Safety/Nudge — only a Health delivery (sourced from an <see cref="Domain.Entities.Alert"/>) carries one.</summary>
    public AlertSeverity? Severity { get; init; }

    public required string DedupKey { get; init; }
    public string? CollapseKey { get; init; }

    public required bool IsWithinQuietHours { get; init; }
    public DateTime? QuietHoursEndUtc { get; init; }
}

public sealed record DeliveryPlan(
    DeliveryChannel Channel,
    string DedupKey,
    string? CollapseKey,
    DateTime? ScheduledFor,
    DateTime ExpiresAt,
    bool AllowCritical,
    bool Escalates);
