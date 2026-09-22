using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A wearable that could screen for atrial fibrillation, on a wearer who has not turned that
/// screening on. The watch raises no irregular-rhythm notifications, CardiTrack therefore raises
/// no rhythm alerts, and every screen shows exactly what it would show for someone whose heart is
/// behaving perfectly.
/// </summary>
/// <remarks>
/// <para>
/// This is the only nudge in the catalogue that exists to correct a <em>reassuring</em> silence.
/// The others close a gap the caregiver can see the shape of — a missing emergency contact, a flat
/// battery, a device that stopped syncing. Here there is nothing to see: the product is quietly
/// not watching for the one finding it would most want to report, and nothing anywhere says so.
/// </para>
/// <para>
/// <b>Only a connection that currently grants the IRN scope is consulted at all</b> — the rule
/// filters on <c>DeviceScopes.GrantsIrn</c> before it looks at either profile column. Those
/// columns are written once from a profile read and never cleared, not on reconnect, not when a
/// scope narrows or is revoked, so a stale value on a now-unscoped connection cannot be trusted in
/// either direction: a stale <c>false</c> would nudge over a device we can no longer ask, and a
/// stale <c>true</c> would call the member covered by a device that may not still be screening.
/// </para>
/// <para>
/// <b>Within a scoped connection, null is not false.</b>
/// <see cref="NudgeConnectionSnapshot.IrnEnrolled"/> is null when its profile has not been read
/// yet or the read failed — which is the permanent state for every connection today, since no
/// connection carries the scope until issue #39's verification lands. Firing on null would tell a
/// family their relative is not being screened when the truth is that we cannot see whether they
/// are, which is a different sentence and a worse one to get wrong. Only an explicit <c>false</c>
/// is a gap.
/// </para>
/// <para>
/// Scoped to the connection rather than the member: enrolment is a setting on one wearer's device
/// and the fix is on that device, so a member with two watches where one screens is covered. The
/// deep link goes to the device the caregiver would have to act on.
/// </para>
/// </remarks>
public sealed class IrnNotEnrolledRule : INudgeRule
{
    public const string Code = "IRN_NOT_ENROLLED";

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Unlock,
        Priority = NotificationPriority.High,

        // Longer than the other Unlock nudges. Turning this on is a change to the wearer's own
        // device, which a caregiver may have to be with them to make, and the answer can honestly
        // be "not yet" for weeks. A fortnight's snooze on that conversation would be nagging.
        DefaultSnooze = TimeSpan.FromDays(30),
        MaxSnooze = TimeSpan.FromDays(180)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        // Scope-gated, not just null-gated. IrnEnrolled/IrnOnboarded are written once from a
        // profile read and never cleared — not on reconnect, not when a scope is narrowed or
        // revoked — so a connection that once granted IRN and later stopped still carries
        // whatever the last read said. Reading either column off a connection that does not
        // currently grant the scope would trust a value nothing today can vouch for: a stale
        // "false" would nudge over a device we can no longer ask, and a stale "true" would call
        // the member covered by a device that may not still be screening at all.
        var live = context.Connections
            .Where(c => c.Status == ConnectionStatus.Connected
                        && DeviceScopes.GrantsIrn(c.Scopes))
            .ToList();

        if (live.Count == 0)
            return NudgeVerdict.NoGap;

        // A connection whose profile we could read and which says the wearer is enrolled covers
        // the member — a second watch that is not enrolled adds nothing to screen with.
        if (live.Any(c => c.IrnEnrolled == true))
            return NudgeVerdict.NoGap;

        // Only a connection that positively reported "not enrolled" is a gap. Nulls — a scoped
        // connection whose profile read has not landed yet or failed — are unknown, and an
        // unknown must not be presented as a finding.
        var target = live
            .Where(c => c.IrnEnrolled == false)
            .OrderBy(c => c.Id)
            .FirstOrDefault();

        if (target is null)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices/{target.Id}",
            discriminator: target.Id.ToString("N"));
    }
}
