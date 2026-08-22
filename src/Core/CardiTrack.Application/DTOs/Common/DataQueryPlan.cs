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
/// The daily metrics a chat reply can chart. Closed enum for the same reason as
/// <see cref="DataQueryKind"/>: the planner picks from a fixed vocabulary, and an unrecognised
/// answer is dropped rather than trusted.
/// </summary>
public enum ChartMetricKind
{
    Steps = 1,
    RestingHeartRate = 2,
    Sleep = 3,

    /// <summary>Overnight heart rate variability (RMSSD), in milliseconds.</summary>
    HeartRateVariability = 4,

    /// <summary>Weight in kilograms, from a connected scale.</summary>
    Weight = 5,

    /// <summary>
    /// The day's lowest blood-sugar reading, in mg/dL — the same figure the dashboard card and the
    /// alert chart plot, so a caregiver who asks about it in chat is shown the series they already
    /// know from the other two surfaces.
    /// </summary>
    BloodGlucose = 6,
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

    /// <summary>
    /// The metrics the question is actually about, when it is about specific ones — a steps
    /// question charts steps, not the member's whole week. Enum-typed like <see cref="Sources"/>:
    /// this shape stays structurally unable to carry free text or an identifier.
    /// </summary>
    /// <remarks>
    /// Three states, deliberately distinguishable: a non-empty list narrows the charts; an empty
    /// list is the model answering "this question is general"; <c>null</c> is the model not
    /// answering at all — an omitted field, or names none of which parsed.
    /// <para>
    /// The two latter states both widen to every fetched series, and that is a choice rather
    /// than an oversight: when the planner has told us nothing, charting nothing would drop data
    /// the caregiver asked to see, and showing an extra series is the cheaper error. Keeping the
    /// states apart in the type is what lets that decision be revisited — and read in telemetry —
    /// without first having to reconstruct which case produced an empty list.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ChartMetricKind>? ChartMetrics { get; init; }
}

/// <summary>The data <see cref="DataQueryPlan"/> resolved to, ready for the clinical prompt and for
/// projecting into chart series.</summary>
public sealed record FetchedMemberData
{
    public IReadOnlyList<CardiTrack.Domain.Entities.ActivityLog> RecentActivity { get; init; } = [];

    /// <summary>
    /// The days <see cref="RecentActivity"/> was read over, inclusive — null when activity was not
    /// among the plan's sources. Carried rather than inferred because the two differ whenever the
    /// member has a gap: a window of seven days with readings on four returns four rows, and a
    /// prompt that counted those rows would tell the model it was looking at four days. The
    /// clinical read is asked to name the stretch it answered for, so it has to be told the
    /// stretch, not a count of what happened to be in it.
    /// </summary>
    public (DateOnly From, DateOnly To)? RecentActivityWindow { get; init; }
    public CardiTrack.Domain.Entities.PatternBaseline? Baseline { get; init; }
    public IReadOnlyList<CardiTrack.Domain.Entities.Alert> UnresolvedAlerts { get; init; } = [];
    public IReadOnlyList<CardiTrack.Domain.Entities.RealtimeAssessment> RealtimeAssessments { get; init; } = [];
}
