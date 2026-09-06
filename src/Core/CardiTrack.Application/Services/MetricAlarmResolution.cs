using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>Where a member's effective alarm came from.</summary>
public enum AlarmProvenance
{
    /// <summary>An account-level default, taken as it stands.</summary>
    Inherited = 1,

    /// <summary>An account-level default this member replaces with their own settings.</summary>
    Overridden = 2,

    /// <summary>An alarm defined for this member alone.</summary>
    MemberOnly = 3,
}

/// <summary>One alarm as it actually applies to one member, with the account row it came from.</summary>
/// <param name="Alarm">
/// The row to evaluate and to show. For <see cref="AlarmProvenance.Overridden"/> this is the
/// member's row, not the account's — the override replaces the default wholesale rather than
/// merging field by field, so what the caregiver sees on the member's screen is what runs.
/// </param>
/// <param name="Source">
/// The account-level row this one overrides, for <see cref="AlarmProvenance.Overridden"/>; null
/// otherwise. Carried so the UI can offer "revert to the account default" without a second fetch.
/// </param>
public sealed record EffectiveAlarm(MetricAlarm Alarm, AlarmProvenance Provenance, MetricAlarm? Source = null);

/// <summary>
/// Folds account-level defaults and per-member rows into the set that actually applies to one
/// CardiMember. Pure, so the inheritance rule is testable without a database — and it is the rule
/// most worth pinning, because getting it wrong either silently drops an alarm a caregiver set or
/// pages them from one they thought they had turned off.
/// <para>
/// The rule: every account-level row applies to every member, unless that member has a row naming
/// it in <c>DerivedFromAlarmId</c>, which replaces it. A member row naming nothing is an addition.
/// An override that is switched off is how a member opts out of an inherited alarm — it stays in
/// the resolved set so the screen can show it as off, and <see cref="Evaluable"/> is what filters
/// it out of the engine.
/// </para>
/// </summary>
public static class MetricAlarmResolution
{
    /// <summary>
    /// The alarms applying to <paramref name="cardiMemberId"/>, given every non-deleted row for
    /// their organization. Ordered by name so two callers list them the same way.
    /// </summary>
    public static IReadOnlyList<EffectiveAlarm> Resolve(
        IEnumerable<MetricAlarm> organizationAlarms, Guid cardiMemberId)
    {
        ArgumentNullException.ThrowIfNull(organizationAlarms);

        var rows = organizationAlarms.Where(a => a.IsActive).ToList();

        var accountDefaults = rows.Where(a => a.CardiMemberId is null).ToList();
        var memberRows = rows.Where(a => a.CardiMemberId == cardiMemberId).ToList();

        // An override is only an override if the account row it names is really there. A member row
        // pointing at a deleted default is treated as the member's own alarm rather than dropped:
        // deleting the account default must not silently delete everyone's tuned copy of it.
        var overridesBySource = memberRows
            .Where(a => a.DerivedFromAlarmId is not null)
            .GroupBy(a => a.DerivedFromAlarmId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var resolved = new List<EffectiveAlarm>();

        foreach (var account in accountDefaults)
        {
            resolved.Add(overridesBySource.TryGetValue(account.Id, out var member)
                ? new EffectiveAlarm(member, AlarmProvenance.Overridden, account)
                : new EffectiveAlarm(account, AlarmProvenance.Inherited));
        }

        var accountIds = accountDefaults.Select(a => a.Id).ToHashSet();
        foreach (var member in memberRows)
        {
            if (member.DerivedFromAlarmId is { } source && accountIds.Contains(source))
                continue;

            resolved.Add(new EffectiveAlarm(member, AlarmProvenance.MemberOnly));
        }

        return resolved
            .OrderBy(e => e.Alarm.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Alarm.Id)
            .ToList();
    }

    /// <summary>The subset the engine actually evaluates. Off means not evaluated at all — no row
    /// is written and nothing is suppressed after the fact, the same stance
    /// <c>AlertPreference</c> takes on the built-in rules.</summary>
    public static IReadOnlyList<EffectiveAlarm> Evaluable(IEnumerable<EffectiveAlarm> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Where(e => e.Alarm.IsEnabled).ToList();
    }
}
