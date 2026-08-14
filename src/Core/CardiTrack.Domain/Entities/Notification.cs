using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One open data-completeness gap, addressed to one user — "CardiTrack is missing X, and here is
/// what supplying it gets you". Separate from <see cref="Alert"/>, which is about the monitored
/// person's body rather than the account's completeness, and which carries an acknowledgment
/// lifecycle this has no use for.
/// </summary>
/// <remarks>
/// Rows are <b>reconciled, not appended</b>. Identity is <see cref="Fingerprint"/> — derived from
/// the rule, the target and the scope — so re-running detection converges on the same row instead
/// of stacking duplicates, and a gap the user closed resolves itself without anyone dismissing it.
/// </remarks>
public class Notification : BaseEntity, ISoftDeletable
{
    public Guid OrganizationId { get; set; }

    /// <summary>Who is being asked to act. One owner per gap — see <c>IsOwner</c>.</summary>
    public Guid UserId { get; set; }

    /// <summary>Null for account-level gaps that belong to no particular member.</summary>
    public Guid? CardiMemberId { get; set; }

    /// <summary>Catalogue key, e.g. <c>DEVICE_STALE_LONG</c>.</summary>
    public string RuleCode { get; set; } = string.Empty;

    /// <summary>
    /// Bumped when a rule's detection or copy changes materially. A muted rule re-arms only when
    /// this increases — that is, only when we have changed the offer rather than repeated it.
    /// </summary>
    public int RuleVersion { get; set; }

    public NotificationCategory Category { get; set; }
    public NotificationPriority Priority { get; set; }

    /// <summary>
    /// SHA-256 of <c>RuleCode | UserId | ScopeId | Discriminator</c>, unique across the table.
    /// Content-derived rather than generated, which is what makes reconciliation idempotent
    /// without a distributed lock.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Localization keys, not rendered copy. Storing English here would freeze wording at
    /// detection time and turn translation into a data migration.
    /// </summary>
    public string TitleKey { get; set; } = string.Empty;
    public string BodyKey { get; set; } = string.Empty;
    public string BenefitKey { get; set; } = string.Empty;

    /// <summary>
    /// JSON substitutions for the copy — <c>{"n":4}</c>. <b>Counters only.</b> No names, no metric
    /// values, no free text: a member's name stored beside <see cref="CardiMemberId"/> and a
    /// health-derived gap would be an identifier-to-clinical join in the clear. Names are resolved
    /// per request at render time instead.
    /// </summary>
    public string TemplateData { get; set; } = "{}";

    /// <summary>Deep link to the exact field that closes the gap, never a settings root.</summary>
    public string ActionDeepLink { get; set; } = string.Empty;

    public NotificationState State { get; set; } = NotificationState.Open;

    public DateTime? SnoozedUntil { get; set; }
    public DateTime? ResolvedDate { get; set; }
    public NotificationResolutionReason? ResolutionReason { get; set; }

    public DateTime FirstDetectedDate { get; set; }
    public DateTime LastEvaluatedDate { get; set; }

    /// <summary>When the user first saw it. The denominator of the comply funnel.</summary>
    public DateTime? FirstSeenDate { get; set; }

    /// <summary>
    /// When this row was handed to the push outbox, for the Safety-class rules that push
    /// (<c>NudgeSpec.PushesWhenOpen</c>). Null means "not pushed for the current arming" — it is
    /// what stops the dispatch sweep sending the same warning every 30 seconds.
    /// </summary>
    /// <remarks>
    /// Cleared when a resolved row reopens (see <c>NudgeReconciler.Refresh</c>), which is the whole
    /// reason it is a column rather than a join against <c>NotificationDelivery</c>. A delivery's
    /// dedup key is matched in any state and never expires, so a fingerprint-derived key alone
    /// would push the first flat battery and silently swallow every one after it — the gap closing
    /// and reopening is exactly the case that matters here.
    /// </remarks>
    public DateTime? PushedDate { get; set; }

    /// <summary>
    /// False for the other caregivers in a family: they see the item read-only, so everyone knows
    /// it is outstanding without five people being nagged to fix one emergency contact.
    /// </summary>
    public bool IsOwner { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public Notification()
    {
        FirstDetectedDate = DateTime.UtcNow;
        LastEvaluatedDate = FirstDetectedDate;
    }

    /// <summary>
    /// Whether this should appear in the inbox now. Snoozed rows stay stored — the snooze has to
    /// expire on its own — but a caller must never treat "row exists" as "show it".
    /// </summary>
    public bool IsVisible(DateTime utcNow) =>
        State == NotificationState.Open ||
        (State == NotificationState.Snoozed && SnoozedUntil <= utcNow);
}
