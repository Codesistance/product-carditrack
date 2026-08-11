using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Everything the rule catalogue is allowed to see about one evaluation target, fetched once
/// before any rule runs.
/// </summary>
/// <remarks>
/// <para>
/// Rules are pure functions of this: no repositories, no clock, no database. That is what lets the
/// whole catalogue be table-tested with no host, and it is why <see cref="UtcNow"/> is a field
/// rather than a call.
/// </para>
/// <para>
/// The snapshots below carry <b>no direct identifiers</b> — no names, no phone numbers, no medical
/// notes, only whether such a value is present. A rule that cannot see a name cannot accidentally
/// persist one into a notification's template data.
/// </para>
/// </remarks>
public sealed record NudgeContext
{
    public required DateTime UtcNow { get; init; }
    public required Guid OrganizationId { get; init; }
    public required NudgeUserSnapshot User { get; init; }

    /// <summary>Null for account-level evaluation, where no member is in scope.</summary>
    public NudgeMemberSnapshot? Member { get; init; }

    public IReadOnlyList<NudgeConnectionSnapshot> Connections { get; init; } = [];
    public IReadOnlyList<NudgeMuteSnapshot> Mutes { get; init; } = [];

    /// <summary>
    /// Whether this user is the one being asked to act, as opposed to a relative who sees the item
    /// read-only. Non-owners are evaluated so the row exists for them, but never nagged.
    /// </summary>
    public bool IsOwner { get; init; } = true;

    /// <summary>Scope a fingerprint hangs off: the member when there is one, else the user.</summary>
    public Guid ScopeId => Member?.Id ?? User.Id;

    public bool IsMemberPaused => Member?.MonitoringPausedUntil > UtcNow;

    /// <summary>
    /// Accounts younger than this are left alone: a caregiver who signed up an hour ago is still
    /// inside the onboarding flow, which owns that conversation.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(48);

    public bool IsInGracePeriod => UtcNow - User.CreatedDate < GracePeriod;
}

public sealed record NudgeUserSnapshot
{
    public required Guid Id { get; init; }
    public required string TimeZoneId { get; init; }
    public required string Locale { get; init; }
    public required DateTime CreatedDate { get; init; }

    /// <summary>
    /// True if at least one of this user's push device tokens is live, OS-authorized, and has the
    /// safety channel enabled. Drives <c>PUSH_UNREACHABLE</c> (notification_engine.md §9) — a
    /// caregiver who silently turned notifications off must not be indistinguishable from one who
    /// is being reached.
    /// </summary>
    public bool HasReachablePushDevice { get; init; } = true;
}

public sealed record NudgeMemberSnapshot
{
    public required Guid Id { get; init; }
    public required DateTime CreatedDate { get; init; }

    /// <summary>Presence only — the notes themselves are PHI and no rule needs to read them.</summary>
    public required bool HasMedicalNotes { get; init; }

    public DateTime? MonitoringPausedUntil { get; init; }

    /// <summary>
    /// Whether this member has ever had a device connection, active or not. Distinguishes
    /// "never got started" — which onboarding owns — from "had one and lost it".
    /// </summary>
    public required bool EverHadConnection { get; init; }

    /// <summary>Distinct days with ingested data inside the baseline window.</summary>
    public required int DaysCaptured { get; init; }

    /// <summary>Most recent day with any ingested data, or null if there has never been one.</summary>
    public DateOnly? LastActivityDate { get; init; }

    /// <summary>True once a non-provisional baseline exists — nothing may alert before then.</summary>
    public required bool HasEstablishedBaseline { get; init; }

    /// <summary>
    /// An open red alert outranks housekeeping: a caregiver dealing with a real event should not
    /// be asked to tidy a profile.
    /// </summary>
    public required bool HasUnacknowledgedRedAlert { get; init; }

    /// <summary>
    /// Whether <c>InactivityDetectionWorker</c> already has an unresolved device-silence alert
    /// open for this member. It notices a quiet wearable within two hours; this engine's own
    /// staleness rule waits two days, so when the alert is standing the nudge would be a second
    /// voice saying a slower version of the same thing.
    /// </summary>
    public bool HasOpenDeviceSilenceAlert { get; init; }
}

public sealed record NudgeConnectionSnapshot
{
    public required Guid Id { get; init; }
    public required DeviceType DeviceType { get; init; }
    public required ConnectionStatus Status { get; init; }
    public DateTime? LastSyncDate { get; init; }

    /// <summary>Granted scopes, already normalised to bundle names (<c>sleep</c>, <c>activity</c>).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
}

public sealed record NudgeMuteSnapshot
{
    public string? RuleCode { get; init; }
    public NotificationCategory? Category { get; init; }
    public Guid? CardiMemberId { get; init; }
    public DateTime? MutedUntil { get; init; }

    public bool Silences(string ruleCode, NotificationCategory category, Guid? memberId, DateTime utcNow)
    {
        if (MutedUntil is not null && MutedUntil <= utcNow)
            return false;

        // A member-scoped mute silences that member only; an unscoped one silences everywhere.
        if (CardiMemberId is not null && CardiMemberId != memberId)
            return false;

        return RuleCode == ruleCode || (Category is not null && Category == category);
    }
}
