using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One inbox item, rendered for the caller.
/// </summary>
/// <remarks>
/// <see cref="CardiMemberName"/> is resolved per request from the member record — it is never read
/// from the stored row, which holds counters only. That split is what keeps a wearer's name out of
/// the same plaintext row as the health-derived gap it describes.
/// </remarks>
public class NotificationResponse
{
    public Guid Id { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public NotificationCategory Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public NotificationState State { get; set; }

    public string TitleKey { get; set; } = string.Empty;
    public string BodyKey { get; set; } = string.Empty;
    public string BenefitKey { get; set; } = string.Empty;

    /// <summary>Substitutions for the copy, as raw JSON. Counters only.</summary>
    public string TemplateData { get; set; } = "{}";

    public Guid? CardiMemberId { get; set; }
    public string? CardiMemberName { get; set; }

    /// <summary>The member's first name — what the app labels them by. <see cref="CardiMemberName"/> stays the full name.</summary>
    public string? CardiMemberFirstName { get; set; }

    public string ActionDeepLink { get; set; } = string.Empty;

    /// <summary>False for Safety rules — the client must not offer "don't ask again" for those.</summary>
    public bool CanMute { get; set; }

    /// <summary>Longest snooze the client may offer, in hours.</summary>
    public int MaxSnoozeHours { get; set; }

    /// <summary>
    /// False when the caller is a relative seeing an item somebody else owns: visible so the family
    /// knows it is outstanding, without five people being asked to fix one thing.
    /// </summary>
    public bool IsOwner { get; set; }

    public DateTime? SnoozedUntil { get; set; }
    public DateTime FirstDetectedDate { get; set; }
    public DateTime? FirstSeenDate { get; set; }
}

public class NotificationListResponse
{
    public List<NotificationResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
}

public class NotificationSummaryResponse
{
    /// <summary>Open, owned, not yet seen — the in-app tab badge.</summary>
    public int UnseenCount { get; set; }

    /// <summary>Every visible owned item, for the inbox header count.</summary>
    public int OpenCount { get; set; }

    /// <summary>
    /// Safety-category items, shown as a persistent banner rather than a card. With one channel,
    /// prominence is the only escalation axis there is, so it is spent here and nowhere else.
    /// </summary>
    public List<NotificationResponse> SafetyBanners { get; set; } = [];

    /// <summary>The top two by priority, for the dashboard's "Complete the picture" card.</summary>
    public List<NotificationResponse> DashboardCards { get; set; } = [];

    /// <summary>
    /// One setup checklist per member the caller watches, for the "Complete the picture" progress
    /// ring — including members whose checklist is complete (<c>Done == Total</c>) or empty
    /// (<c>Total == 0</c>), which the client hides. Ordered by first name.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DashboardCards"/>, this is computed from the member's current data rather
    /// than read from stored notifications, so it can tell "done" from "not applicable" and does
    /// not go quiet while a nudge is being held back.
    /// </remarks>
    public List<MemberSetupProgress> MemberSetup { get; set; } = [];
}

/// <summary>One member's setup checklist — "Pop's profile 3 of 5 · Next: emergency contact".</summary>
public class MemberSetupProgress
{
    public Guid CardiMemberId { get; set; }

    /// <summary>What the app labels the member by.</summary>
    public string CardiMemberFirstName { get; set; } = string.Empty;

    /// <summary>
    /// Same meaning as <see cref="NotificationResponse.IsOwner"/>: false when the caller is a
    /// relative and somebody else is the one asked to finish this member's setup. The progress is
    /// shown to them all the same; the client should not offer the "Next" step as a call to action.
    /// </summary>
    public bool IsOwner { get; set; }

    /// <summary>How many of <see cref="Steps"/> are done.</summary>
    public int Done { get; set; }

    /// <summary>
    /// How many steps apply to this member. Steps that do not apply (no connected device to grant
    /// sleep) and steps the caller has muted are left out, not counted as done.
    /// </summary>
    public int Total { get; set; }

    /// <summary>The applicable steps in the nudges' priority order; the first not done is "Next".</summary>
    public List<MemberSetupStep> Steps { get; set; } = [];
}

/// <summary>One step on a member's setup checklist.</summary>
public class MemberSetupStep
{
    /// <summary>
    /// Stable identifier — <c>emergency-contact</c>, <c>sleep-access</c>, <c>irregular-rhythm</c>,
    /// <c>time-zone</c>, <c>medical-information</c>. Key client copy off this.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>A short English label, for a client with no copy of its own for <see cref="Key"/>.</summary>
    public string Title { get; set; } = string.Empty;

    public bool Done { get; set; }

    /// <summary>
    /// The screen that closes the step — the same link the step's nudge carries — present whether
    /// or not the step is done, so a finished step can still be opened to review.
    /// </summary>
    public string ActionDeepLink { get; set; } = string.Empty;
}

public class NotificationMuteResponse
{
    public Guid Id { get; set; }
    public string? RuleCode { get; set; }
    public NotificationCategory? Category { get; set; }
    public Guid? CardiMemberId { get; set; }
    public string? CardiMemberName { get; set; }

    /// <summary>The member's first name — what the app labels them by. <see cref="CardiMemberName"/> stays the full name.</summary>
    public string? CardiMemberFirstName { get; set; }
    public DateTime MutedDate { get; set; }
    public DateTime? MutedUntil { get; set; }
}
