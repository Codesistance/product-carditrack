namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// Which existing, already-audited data sources answer a member-chat question. Closed enum —
/// mirrors exactly what <c>MemberContextComposer</c>'s registered sources and
/// <c>DataQueryWhitelist</c>'s repository calls already expose. Adding a fifth source means adding
/// a member here and a case in <c>DataQueryWhitelist.Execute</c>, not opening a new access path.
/// </summary>
public enum DataQueryKind
{
    RecentActivity = 1,
    Baseline = 2,
    UnresolvedAlerts = 3,
    RealtimeAssessments = 4,
}

/// <summary>
/// What one caregiver question needs fetched, as decided by <c>IDataQueryPlanner</c> and enforced
/// by <c>DataQueryWhitelist</c>. The type is the security boundary: it is structurally incapable of
/// naming <em>whose</em> data to fetch, only <em>which kinds</em>. The CardiMember the plan runs
/// against always comes from the authenticated caller, never from this type — see
/// <c>MemberChatService.SendMessageAsync</c>. Do not add a member/user identifier field to this
/// record. If a future source genuinely needs one, that is a sign it does not belong in this
/// closed-enum shape at all, not a reason to add one here — see the security review that
/// established this constraint (member-chat planning notes, 2026-08-20).
/// </summary>
public sealed record DataQueryPlan
{
    public required IReadOnlyList<DataQueryKind> Sources { get; init; }

    /// <summary>Clamped by <c>DataQueryWhitelist</c> regardless of what the model asked for.</summary>
    public int RecentActivityDays { get; init; } = 7;

    /// <summary>Clamped by <c>DataQueryWhitelist</c> regardless of what the model asked for.</summary>
    public int RealtimeAssessmentHours { get; init; } = 24;
}

/// <summary>The data <see cref="DataQueryPlan"/> resolved to, ready for the clinical prompt and for
/// projecting into chart series.</summary>
public sealed record FetchedMemberData
{
    public IReadOnlyList<CardiTrack.Domain.Entities.ActivityLog> RecentActivity { get; init; } = [];
    public CardiTrack.Domain.Entities.PatternBaseline? Baseline { get; init; }
    public IReadOnlyList<CardiTrack.Domain.Entities.Alert> UnresolvedAlerts { get; init; } = [];
    public IReadOnlyList<CardiTrack.Domain.Entities.RealtimeAssessment> RealtimeAssessments { get; init; } = [];
}
