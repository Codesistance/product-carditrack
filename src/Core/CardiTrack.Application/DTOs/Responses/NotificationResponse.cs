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
}

public class NotificationMuteResponse
{
    public Guid Id { get; set; }
    public string? RuleCode { get; set; }
    public NotificationCategory? Category { get; set; }
    public Guid? CardiMemberId { get; set; }
    public string? CardiMemberName { get; set; }
    public DateTime MutedDate { get; set; }
    public DateTime? MutedUntil { get; set; }
}
