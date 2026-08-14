using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A wearable about to run flat. Monitoring is minutes-to-hours from stopping, and once it does
/// the dashboard looks exactly like a quiet day — the same indistinguishability that makes
/// <see cref="DeviceAuthBrokenRule"/> Safety class, reached by a different route.
/// </summary>
/// <remarks>
/// <para>
/// Safety class, and deliberately not an <c>Alert</c>. The <c>Alert</c> entity records events
/// about the <em>member</em> — heart rate, sleep, inactivity, broken patterns — and a caregiver
/// reading an alert history should see a clinical record, not interleaved hardware chores. Routing
/// this as a Safety nudge costs nothing in urgency: <see cref="DeliveryPlanner"/> gives
/// <see cref="DeliveryCategory.Safety"/> an immediate push, the critical APNs flag, quiet-hours
/// override, a 30-minute TTL and escalation — the same treatment a red alert gets, and strictly
/// more than an orange one.
/// </para>
/// <para>
/// This is the one warning in the set the caregiver can still act on: unlike a revoked grant or a
/// silent device, a charging cable fixes it, and it is worth saying <em>before</em> the data stops
/// rather than two hours after <c>InactivityDetectionWorker</c> notices the silence.
/// </para>
/// </remarks>
public sealed class DeviceBatteryLowRule : INudgeRule
{
    public const string Code = "DEVICE_BATTERY_LOW";

    public string RuleCode => Code;
    public int Version => 1;
    public NudgeSpec Spec { get; } = NudgeSpec.Safety() with { PushesWhenOpen = true };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        // A broken grant outranks a flat battery, exactly as DeviceStaleLongRule defers to it:
        // told both, a caregiver would charge a watch whose real problem is that we lost
        // permission to read it, then find the data still missing.
        var hasBrokenGrant = context.Connections
            .Any(c => c.Status is ConnectionStatus.TokenExpired or ConnectionStatus.AuthError);

        if (hasBrokenGrant)
            return NudgeVerdict.NoGap;

        var worst = context.Connections
            .Where(c => c.Status == ConnectionStatus.Connected)
            // A reading only means anything while it is current. Past the freshness window the
            // device has stopped syncing entirely, which DeviceStaleLongRule and the device-silence
            // alert both own — and a percentage captured before the last charge would be a lie.
            .Where(c => DeviceBattery.IsFresh(c.BatteryUpdatedAt, context.UtcNow))
            .Select(c => (Connection: c, Tier: DeviceBattery.GetTier(c.BatteryLevel, c.BatteryStatus)))
            .Where(x => x.Tier is not null)
            // Worst tier first; a tie breaks on the lower percentage, then on id so it resolves
            // the same way on every evaluation — the discriminator below is what keeps one
            // device's warning from replacing another's.
            .OrderByDescending(x => x.Tier)
            .ThenBy(x => x.Connection.BatteryLevel ?? int.MaxValue)
            .ThenBy(x => x.Connection.Id)
            .FirstOrDefault();

        if (worst is not { Tier: { } tier })
            return NudgeVerdict.NoGap;

        // The tier names the severity; the suffix names the one thing that changes what the
        // sentence can say. batteryLevel and batteryStatus are independently optional on the
        // provider's schema, so a variant with no percentage to report must not share a template
        // with one that has to — a "{percent}%" placeholder nothing ever fills reaches the
        // caregiver verbatim. Empty gets its own suffix too: the device has already stopped, which
        // is a materially different sentence from "at 8%" even though both are Critical.
        var isEmpty = string.Equals(
            worst.Connection.BatteryStatus, DeviceBattery.EmptyStatus, StringComparison.OrdinalIgnoreCase);
        var tierName = tier.ToString().ToLowerInvariant();
        var variant = (isEmpty, worst.Connection.BatteryLevel) switch
        {
            (true, _) => "critical_empty",
            (false, { } level) => tierName,
            (false, null) => $"{tierName}_unknown",
        };
        var templateData = worst.Connection.BatteryLevel is { } percent
            ? new Dictionary<string, object> { ["percent"] = percent }
            : new Dictionary<string, object>();

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
            discriminator: worst.Connection.Id.ToString("N"),
            variant: variant,
            templateData: templateData,
            priority: DeviceBattery.PriorityFor(tier));
    }
}
