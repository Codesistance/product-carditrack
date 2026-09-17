using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>What the settings rung says, and the change it is waiting on a yes for, if any.</summary>
public sealed record AlertSettingsReply(string Reply, PendingAlertChange? Pending = null);

/// <summary>
/// The settings rung's replies, assembled in code from a plan and a snapshot — the same
/// reply-composition-policy-with-no-I/O shape as <see cref="MemberChatReplies"/>, beside it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here writes. The rung proposes: it describes the one change it understood, in the
/// catalogue's titles and <see cref="MetricAlarmNarrative.Condition(SaveMetricAlarmRequest)"/>'s
/// sentence, and asks for a yes. The alert services apply it on the next turn, with the same
/// primary-caregiver check the settings pages go through. A model that mis-hears "sleep" as
/// "steps" therefore costs a caregiver one "no", never a silenced rule.
/// </para>
/// <para>
/// The proposal is written from the request that will actually be sent, so the sentence a
/// caregiver agrees to and the alarm that gets saved cannot describe two different things — the
/// same reason the builder previews through <c>MetricAlarmNarrative</c>.
/// </para>
/// </remarks>
public static class AlertSettingsComposer
{
    /// <summary>What a yes or no is answered with.</summary>
    public const string ConfirmPrompt = "Reply yes to do it, or no to leave things as they are.";

    /// <summary>The wake-the-family line every red proposal carries — the same warning the
    /// builder puts behind its confirmation.</summary>
    public const string RedSeverityWarning =
        "Because it's red, it will push through quiet hours and go to the other caregivers if "
        + "nobody acknowledges it.";

    public static AlertSettingsReply Compose(
        AlertChangePlan plan,
        AlertSettingsSnapshot snapshot,
        bool canManage,
        string? firstName,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);

        var subject = Subject(firstName);

        switch (plan.Action)
        {
            case AlertChangeAction.List:
                return new AlertSettingsReply(ListReply(snapshot, subject, canManage));

            case AlertChangeAction.NotificationSettings:
                return new AlertSettingsReply(
                    "Quiet hours and which kinds of alert reach your phone are settings on your own "
                    + "account rather than on " + Possessive(firstName) + " alerts, so I can't change "
                    + "them from here — they're under Settings, then Notifications. I can switch "
                    + $"{Possessive(firstName)} own alerts on or off, or set an alarm on a reading.");

            case AlertChangeAction.Unclear:
                return new AlertSettingsReply(UnclearReply(subject));
        }

        if (!canManage)
            return new AlertSettingsReply(ReadOnlyReply(snapshot, firstName));

        return plan.Action switch
        {
            AlertChangeAction.EnableRule => ProposeRule(plan, snapshot, subject, enabled: true, utcNow),
            AlertChangeAction.DisableRule => ProposeRule(plan, snapshot, subject, enabled: false, utcNow),
            AlertChangeAction.CreateAlarm => ProposeNewAlarm(plan, snapshot, subject, utcNow),
            AlertChangeAction.EditAlarm => ProposeEdit(plan, snapshot, subject, utcNow),
            AlertChangeAction.EnableAlarm => ProposeSwitch(plan, snapshot, subject, enabled: true, utcNow),
            AlertChangeAction.DisableAlarm => ProposeSwitch(plan, snapshot, subject, enabled: false, utcNow),
            AlertChangeAction.DeleteAlarm => ProposeDelete(plan, snapshot, subject, utcNow),
            _ => new AlertSettingsReply(UnclearReply(subject)),
        };
    }

    /// <summary>
    /// What a caregiver who may view but not manage the member is told, whatever they asked for:
    /// who can change things, and the list. Served without a planning call — there is no change
    /// to plan, and a viewer's request is not worth a model's look at the configuration.
    /// </summary>
    public static string ReadOnlyReply(AlertSettingsSnapshot snapshot, string? firstName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return $"Only {Possessive(firstName)} primary caregiver can change what's watching them. "
            + ListReply(snapshot, Subject(firstName), canManage: false);
    }

    /// <summary>The "done" line for a confirmed change — what happened, in the past tense the
    /// proposal's <see cref="PendingAlertChange.Done"/> carries, with a tail that fits the kind.</summary>
    public static string AppliedReply(PendingAlertChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var what = change.Done ?? LowerFirst(change.Summary);
        var tail = change.Kind switch
        {
            PendingAlertChangeKind.SetRule => "You can change it back any time here or from Alert settings.",
            PendingAlertChangeKind.DeleteAlarm => "Alert settings shows what applies now.",
            _ => "It's in Alert settings if you want to fine-tune it.",
        };
        return $"Done — I've {what}. {tail}";
    }

    /// <summary>What the proposal was about is not as it was — retuned, switched or removed by
    /// someone else inside the window — so the yes applies nothing.</summary>
    public static string ChangedSinceProposedReply() =>
        "That's been changed since I suggested this, so I've left it alone — ask me again and "
        + "I'll look at it as it is now.";

    /// <summary>A yes or no that arrived after the proposal had already been taken — by an
    /// earlier answer, or by the same answer sent twice.</summary>
    public static string AlreadyHandledReply() =>
        "I've already dealt with that one — tell me again what you'd like if there's more to do.";

    public static string CancelledReply() =>
        "Okay, I've left everything as it was.";

    /// <summary>A yes that arrived after the proposal stopped being current.</summary>
    public static string LapsedReply() =>
        "That was a little while ago, so I haven't changed anything — tell me again what you'd "
        + "like and I'll set it up.";

    /// <summary>The apply step was refused — the caregiver is no longer the primary, or the row
    /// went away between proposing and confirming.</summary>
    public static string CouldNotApplyReply(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "I couldn't make that change just now — nothing has been altered. Alert settings is "
              + "the place to try it directly."
            : $"I couldn't make that change: {TrimStop(reason)}. Nothing has been altered.";

    // ── proposals ─────────────────────────────────────────────────────────────────────────

    private static AlertSettingsReply ProposeRule(
        AlertChangePlan plan, AlertSettingsSnapshot snapshot, string subject, bool enabled, DateTime utcNow)
    {
        var rule = snapshot.FindRule(plan.RuleId);
        if (rule is null)
            return new AlertSettingsReply(WhichRuleReply(snapshot, subject));

        if (!rule.IsImplemented)
        {
            return new AlertSettingsReply(
                $"“{rule.Title}” isn't available yet — it's listed in Alert settings as coming soon, "
                + "so there's nothing to switch for now.");
        }

        if (rule.Enabled == enabled)
        {
            return new AlertSettingsReply(
                $"“{rule.Title}” is already {OnOff(enabled)} for {subject}, so there's nothing to change.");
        }

        var summary = $"Switch{(enabled ? " on" : " off")} “{rule.Title}” for {subject} — {LowerFirst(rule.Description)}";
        return Proposal(summary, new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SetRule,
            RuleId = rule.Id,
            Enabled = enabled,
            Summary = summary,
            Done = $"switched {OnOff(enabled)} “{rule.Title}” for {subject}",
            ProposedAtUtc = utcNow,
        });
    }

    private static AlertSettingsReply ProposeNewAlarm(
        AlertChangePlan plan, AlertSettingsSnapshot snapshot, string subject, DateTime utcNow)
    {
        var request = AlarmSuggestedDefaults.Build(plan, out var missing);
        if (request is null)
            return new AlertSettingsReply(MissingReply(missing, subject));

        if (snapshot.EnabledAlarmCount >= MetricAlarmValidation.MaxEnabledAlarmsPerMember)
        {
            return new AlertSettingsReply(
                $"{Capitalise(subject)} already has {MetricAlarmValidation.MaxEnabledAlarmsPerMember} alarms "
                + "switched on, which is the most one person can carry. Switch one off first and I'll add this one.");
        }

        if (Refusal(request) is { } refusal)
            return new AlertSettingsReply(refusal);

        var summary = $"Add an alarm for {subject} called “{request.Name}”: {LowerFirst(MetricAlarmNarrative.Condition(request))} "
            + $"It would show as {Severity(request.Severity)}";
        return Proposal(summary, new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.CreateAlarm,
            Alarm = request,
            Summary = summary,
            Done = $"added an alarm for {subject} called “{request.Name}”",
            ProposedAtUtc = utcNow,
        }, request.Severity);
    }

    private static AlertSettingsReply ProposeEdit(
        AlertChangePlan plan, AlertSettingsSnapshot snapshot, string subject, DateTime utcNow)
    {
        var entry = snapshot.FindAlarm(plan.AlarmLabel);
        if (entry is null)
            return new AlertSettingsReply(WhichAlarmReply(snapshot, subject));

        var request = AlarmSuggestedDefaults.Revise(entry.Row, plan, out var missing);
        if (request is null)
            return new AlertSettingsReply(MissingReply(missing, subject));

        if (Refusal(request) is { } refusal)
            return new AlertSettingsReply(refusal);

        var renamed = !string.Equals(request.Name, entry.Row.Name, StringComparison.Ordinal);
        var summary = renamed && MetricAlarmNarrative.Condition(request) == entry.Row.Condition
            ? $"Rename “{entry.Row.Name}” to “{request.Name}” for {subject}"
            : $"Change “{entry.Row.Name}” for {subject} to: {LowerFirst(MetricAlarmNarrative.Condition(request))} "
              + $"It would show as {Severity(request.Severity)}";
        return Proposal(summary, new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SaveAlarm,
            AlarmId = entry.Row.Id,
            Alarm = request,
            AlarmFingerprint = MetricAlarmFingerprint.Of(entry.Row),
            Summary = summary,
            Done = renamed && MetricAlarmNarrative.Condition(request) == entry.Row.Condition
                ? $"renamed “{entry.Row.Name}” to “{request.Name}” for {subject}"
                : $"changed “{entry.Row.Name}” for {subject} to: {LowerFirst(TrimStop(MetricAlarmNarrative.Condition(request)))}",
            ProposedAtUtc = utcNow,
        }, request.Severity);
    }

    private static AlertSettingsReply ProposeSwitch(
        AlertChangePlan plan, AlertSettingsSnapshot snapshot, string subject, bool enabled, DateTime utcNow)
    {
        var entry = snapshot.FindAlarm(plan.AlarmLabel);
        if (entry is null)
            return new AlertSettingsReply(WhichAlarmReply(snapshot, subject));

        if (entry.Row.IsEnabled == enabled)
        {
            return new AlertSettingsReply(
                $"“{entry.Row.Name}” is already {OnOff(enabled)} for {subject}, so there's nothing to change.");
        }

        if (enabled && snapshot.EnabledAlarmCount >= MetricAlarmValidation.MaxEnabledAlarmsPerMember)
        {
            return new AlertSettingsReply(
                $"{Capitalise(subject)} already has {MetricAlarmValidation.MaxEnabledAlarmsPerMember} alarms "
                + "switched on, which is the most one person can carry. Switch one off first.");
        }

        var summary = $"Switch {OnOff(enabled)} “{entry.Row.Name}” for {subject} — {LowerFirst(entry.Row.Condition)}";
        // Switching an opted-out override back on with nothing else changed puts the account's
        // version back — the alarm service's rule, shared with the list page's toggle — and the
        // caregiver is told so here rather than finding their tuning gone.
        var restoresDefault = enabled && entry.Row.Provenance is AlarmProvenance.Overridden;
        if (restoresDefault)
            summary = $"{TrimStop(summary)} — the account's version applies again";
        // Switching a red alarm back on is agreeing to what red means, so the warning travels
        // with the proposal exactly as it does for a new red alarm. Switching off needs none.
        return Proposal(TrimStop(summary), new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SaveAlarm,
            AlarmId = entry.Row.Id,
            Alarm = AlarmSuggestedDefaults.Switched(entry.Row, enabled),
            AlarmFingerprint = MetricAlarmFingerprint.Of(entry.Row),
            Summary = TrimStop(summary),
            Done = restoresDefault
                ? $"switched on “{entry.Row.Name}” for {subject}, with the account's version applying again"
                : $"switched {OnOff(enabled)} “{entry.Row.Name}” for {subject}",
            ProposedAtUtc = utcNow,
        }, enabled ? entry.Row.Severity : null);
    }

    private static AlertSettingsReply ProposeDelete(
        AlertChangePlan plan, AlertSettingsSnapshot snapshot, string subject, DateTime utcNow)
    {
        var entry = snapshot.FindAlarm(plan.AlarmLabel);
        if (entry is null)
            return new AlertSettingsReply(WhichAlarmReply(snapshot, subject));

        // An inherited default is the account's, not this member's: nothing of theirs to remove.
        // Switching it off for them is what "get rid of it" can mean here.
        if (entry.Row.Provenance is AlarmProvenance.Inherited)
        {
            if (!entry.Row.IsEnabled)
            {
                return new AlertSettingsReply(
                    $"“{entry.Row.Name}” is shared across your account and is already off for {subject}. "
                    + "Removing it for everyone is done from the account's alarms in Alert settings.");
            }

            var offSummary = $"Switch off “{entry.Row.Name}” for {subject} — it's shared across your account, "
                + "so I'd switch it off for them rather than remove it";
            return Proposal(offSummary, new PendingAlertChange
            {
                Kind = PendingAlertChangeKind.SaveAlarm,
                AlarmId = entry.Row.Id,
                Alarm = AlarmSuggestedDefaults.Switched(entry.Row, enabled: false),
                AlarmFingerprint = MetricAlarmFingerprint.Of(entry.Row),
                Summary = offSummary,
                Done = $"switched off “{entry.Row.Name}” for {subject}",
                ProposedAtUtc = utcNow,
            });
        }

        var summary = entry.Row.Provenance is AlarmProvenance.Overridden
            ? $"Put the account's version of “{entry.Row.Name}” back for {subject}, dropping the changes made for them"
            : $"Remove the alarm “{entry.Row.Name}” for {subject}";
        return Proposal(summary, new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.DeleteAlarm,
            AlarmId = entry.Row.Id,
            AlarmFingerprint = MetricAlarmFingerprint.Of(entry.Row),
            Summary = summary,
            Done = entry.Row.Provenance is AlarmProvenance.Overridden
                ? $"put the account's version of “{entry.Row.Name}” back for {subject}"
                : $"removed the alarm “{entry.Row.Name}” for {subject}",
            ProposedAtUtc = utcNow,
        });
    }

    private static AlertSettingsReply Proposal(string summary, PendingAlertChange pending, AlertSeverity? severity = null)
    {
        // One stop, whatever the summary ended on: the alarm summaries close on the severity
        // gloss, which carries its own.
        var text = $"Here's what I'd do: {LowerFirst(TrimStop(summary))}.";
        if (severity == AlertSeverity.Red)
            text += $" {RedSeverityWarning}";
        return new AlertSettingsReply($"{text} {ConfirmPrompt}", pending);
    }

    /// <summary>The builder's own refusal for an alarm that cannot be saved, or null when it can.</summary>
    private static string? Refusal(SaveMetricAlarmRequest request)
    {
        var errors = MetricAlarmValidation.Validate(request);
        if (errors.Count == 0)
            return null;

        return $"I can't set that one up as it stands: {LowerFirst(TrimStop(errors[0].Message))}. "
            + "Tell me the adjusted version, or build it in Alert settings.";
    }

    // ── read-only replies ─────────────────────────────────────────────────────────────────

    private static string ListReply(AlertSettingsSnapshot snapshot, string subject, bool canManage)
    {
        var implemented = snapshot.Rules.Where(r => r.IsImplemented).ToList();
        var on = implemented.Where(r => r.Enabled).Select(r => r.Title).ToList();
        var off = implemented.Where(r => !r.Enabled).Select(r => r.Title).ToList();

        var rules = $"CardiTrack's own alerts for {subject} — on: {(on.Count > 0 ? Join(on) : "none")}"
            + (off.Count > 0 ? $"; off: {Join(off)}." : ".");

        // Provenance is said per row: an account default reaches every member and was not
        // necessarily set by this caregiver, and "you've set" would claim it was.
        string alarms;
        if (snapshot.Alarms.Count == 0)
        {
            alarms = "No alarms yet.";
        }
        else
        {
            var lines = snapshot.Alarms.Select(a =>
                $"“{a.Row.Name}” — {LowerFirst(TrimStop(a.Row.Condition))} "
                + $"({OnOff(a.Row.IsEnabled)}{ProvenanceNote(a.Row.Provenance, subject)})");
            alarms = $"Alarms: {string.Join("; ", lines)}.";
        }

        var closing = canManage
            ? " Ask me to switch any of these on or off, or to add an alarm on a reading — "
              + "\"alert me if their heart rate goes over 120\", say."
            : string.Empty;

        return $"{rules} {alarms}{closing}";
    }

    private static string UnclearReply(string subject) =>
        $"I can change what's watching {subject}, but I couldn't tell which alert you meant. You can "
        + "switch one of CardiTrack's own alerts on or off — \"turn off the activity decline alert\" — "
        + "or set an alarm on a reading — \"alert me if their heart rate goes over 120\". Or ask me "
        + "which alerts are on.";

    private static string WhichRuleReply(AlertSettingsSnapshot snapshot, string subject)
    {
        var titles = snapshot.Rules.Where(r => r.IsImplemented).Select(r => r.Title).ToList();
        return $"Which of CardiTrack's alerts do you mean? For {subject} they are: {Join(titles)}.";
    }

    private static string WhichAlarmReply(AlertSettingsSnapshot snapshot, string subject)
    {
        if (snapshot.Alarms.Count == 0)
        {
            return $"There are no alarms set for {subject} yet, so there's nothing to change — "
                + "tell me what to watch and I'll set one up.";
        }

        var names = snapshot.Alarms.Select(a => $"“{a.Row.Name}”").ToList();
        return $"Which alarm do you mean? {Capitalise(subject)} has {Join(names)}.";
    }

    private static string MissingReply(IReadOnlyList<string> missing, string subject) =>
        $"I can set that alarm up for {subject} — I just need {Join(missing)}. Tell me and I'll put it together.";

    // ── words ─────────────────────────────────────────────────────────────────────────────

    private static string Subject(string? firstName) =>
        string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

    private static string Possessive(string? firstName) =>
        string.IsNullOrWhiteSpace(firstName) ? "their" : $"{firstName}'s";

    private static string OnOff(bool enabled) => enabled ? "on" : "off";

    private static string ProvenanceNote(AlarmProvenance? provenance, string subject) => provenance switch
    {
        AlarmProvenance.Inherited => ", shared across the account",
        AlarmProvenance.Overridden => $", tuned for {subject}",
        _ => string.Empty,
    };

    private static string Severity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Red => "red — the most urgent kind.",
        AlertSeverity.Orange => "orange — worth prompt attention.",
        _ => "yellow — a check-in, not an emergency.",
    };

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    private static string LowerFirst(string text) =>
        text.Length > 0 && char.IsUpper(text[0]) && !(text.Length > 1 && char.IsUpper(text[1]))
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;

    private static string Capitalise(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static string TrimStop(string text) =>
        text.TrimEnd().TrimEnd('.');
}
