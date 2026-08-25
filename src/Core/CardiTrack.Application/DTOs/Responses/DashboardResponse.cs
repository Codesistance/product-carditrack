namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Composed payload for the mobile Main Dashboard (M1-09): hero status, key metrics with
/// daily series, recent alerts, and device/baseline state in a single round-trip.
/// Status/severity fields are lowercase strings (green/yellow/orange/red/unknown) per the
/// REST contract in docs/execution/backend/api.
/// </summary>
public class DashboardResponse
{
    public Guid CardiMemberId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }

    /// <summary>
    /// The number behind the dashboard's dedicated Emergency Call action (issue #67, reworked by
    /// issue #162 into its own visually distinct tile once <see cref="Phone"/> existed to power
    /// the plain "Call"/"Message" actions instead).
    /// </summary>
    public string? EmergencyContactPhone { get; set; }

    /// <summary>Who <see cref="EmergencyContactPhone"/> belongs to, so the UI can say.</summary>
    public string? EmergencyContactName { get; set; }

    /// <summary>
    /// The CardiMember's own phone, distinct from <see cref="EmergencyContactPhone"/> — behind
    /// the dashboard's "Call" and "Message" actions. Currently only captured on the Edit
    /// CardiMember screen (M1-14); onboarding (M1-04) does not collect it yet, so this is null
    /// for any member who hasn't since had it added. Same graceful-absence handling as the
    /// emergency contact.
    /// </summary>
    public string? Phone { get; set; }

    public string? PhotoUrl { get; set; }
    /// <summary>green/yellow/orange/red/unknown, or "paused" while monitoring is paused.</summary>
    public string HealthStatus { get; set; } = "unknown";

    public bool MonitoringPaused { get; set; }
    public DateTime? MonitoringPausedUntil { get; set; }
    public string? MonitoringPauseReason { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// Deterministic data-pipeline freshness, independent of <see cref="HealthStatus"/>'s clinical
    /// severity: red/amber = no sync in 12h/4h, blue = synced but not yet assessed, green = the
    /// latest sync has been assessed. Drives the CardiMember card's progress-bar caption.
    /// </summary>
    public string DataFreshness { get; set; } = "red";
    public string DataFreshnessMessage { get; set; } = string.Empty;

    public int UnreadAlertCount { get; set; }

    /// <summary>
    /// Whether this member has a current wellness suggestion on the CardiMember Details Quick
    /// actions card (<c>GET api/v1/insights/members/{id}/advise</c>). A plain
    /// existence-and-freshness check against the persisted <c>MemberAdvise</c> row — same
    /// staleness ceiling as the read endpoint (<c>AdviseStaleness.MaxAge</c>, shared so the two
    /// can't drift) — never a model call, so the Dashboard card's pulse indicator costs nothing
    /// beyond what this response already pays for.
    /// </summary>
    public bool HasAdvise { get; set; }

    /// <summary>
    /// When the suggestion behind <see cref="HasAdvise"/> was generated — the same row and the
    /// same stamp the advise endpoint serves as <c>AdviseResponse.GeneratedAt</c>. What lets the
    /// Dashboard card tell a suggestion the caregiver has already read from a new one, so its
    /// pulse can stop once the suggestion has been seen. Null whenever <see cref="HasAdvise"/> is
    /// false.
    /// </summary>
    public DateTimeOffset? AdviseGeneratedAt { get; set; }
    public DashboardDeviceState Device { get; set; } = new();
    public DashboardBaselineState Baseline { get; set; } = new();
    public DashboardMetrics? Metrics { get; set; }
    public List<DashboardAlertSummary> RecentAlerts { get; set; } = new();

    /// <summary>
    /// Conditions from the member's last GPS-tagged exercise session — not live weather, and
    /// never a stored coordinate (see <see cref="EnvironmentalReading"/>). Null when the member
    /// hasn't granted environmental-context consent, or nothing has been derived yet.
    /// </summary>
    public WeatherSnapshotResponse? Weather { get; set; }

    /// <summary>
    /// The question still waiting on this family, if any — at most one per member (see
    /// <see cref="QuestionnairesPageResponse.Pending"/>). Drives the CardiMember card's Q&amp;A
    /// icon: its pulse, its badge, and what opens when a caregiver taps it.
    /// </summary>
    public QuestionnaireResponse? PendingQuestionnaire { get; set; }

    /// <summary>
    /// The good news, when there is any: nothing has been raised about this member for a while
    /// and every part of the pipeline that would have raised it was running. Null the rest of the
    /// time — including for a member who is simply new, paused, or whose device has gone quiet,
    /// none of whom may be told they are fine. See <see cref="Services.QuietStretch"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="HealthStatus"/>. Green already means "no unresolved
    /// alerts right now", which is true on the first green morning after a bad week; this says how
    /// long that has held, which is a different and much more reassuring claim, and the client
    /// shows it where the Recent Alerts strip would otherwise just be absent.
    /// </remarks>
    public ReassuranceResponse? Reassurance { get; set; }

    public DateTime GeneratedAt { get; set; }
}

/// <summary>
/// A stretch with nothing to report, as the apps read it. Carries the numbers rather than a
/// sentence: the copy lives with the card that shows it, next to the per-tier status copy it sits
/// beneath, so the two are written in one voice and neither is a string this DTO froze.
/// </summary>
public class ReassuranceResponse
{
    /// <summary>Whole days since anything was last raised — at least
    /// <see cref="Services.QuietStretch.MinimumDays"/>.</summary>
    public int QuietDays { get; set; }

    /// <summary>
    /// What the stretch is measured from: the last alert, or the member coming under watch when
    /// there has never been one.
    /// </summary>
    public DateTime QuietSince { get; set; }

    /// <summary>
    /// False when this member has never had an alert at all, so the client can say "since we
    /// started watching" rather than implying an episode ended that never began.
    /// </summary>
    public bool FollowsAnAlert { get; set; }
}

/// <summary>
/// Last-known weather for a member, derived from their most recent exercise session — carries
/// no coordinate, and is not live weather. Shared between <see cref="DashboardResponse"/> and
/// <see cref="CardiMemberDetailResponse"/> so both screens read it the same way. Plain data only
/// — built by <see cref="CardiTrack.Application.Services.WeatherSnapshotMapper"/>, not by a
/// domain-referencing factory here, so this DTO stays independent of the domain model.
/// </summary>
public class WeatherSnapshotResponse
{
    public decimal? TemperatureCelsius { get; set; }

    /// <summary>Free text from the weather provider ("Light rain", "Partly cloudy") — described
    /// to a reader, never matched against, same stance as the AI prompt context that reads it.</summary>
    public string? Condition { get; set; }

    public int? HumidityPercent { get; set; }
    public int? AirQualityIndex { get; set; }
    public string? AirQualityCategory { get; set; }

    /// <summary>When the session that produced this reading ended — clients use this to say
    /// "as of" rather than implying the conditions are current.</summary>
    public DateTime AsOfUtc { get; set; }
}

public class DashboardDeviceState
{
    public bool HasActiveConnection { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceName { get; set; }
    public string? ConnectionStatus { get; set; }
    public DateTime? LastSyncDate { get; set; }
}

public class DashboardBaselineState
{
    public bool IsLearning { get; set; }

    /// <summary>
    /// True while the metrics are coloured against a baseline shorter than
    /// <see cref="DaysRequired"/> days — an early impression, not an established normal.
    /// Clients should caveat comparisons accordingly.
    /// </summary>
    public bool IsProvisional { get; set; }

    /// <summary>Window (in days) of the baseline in use; null while still learning.</summary>
    public int? BaselinePeriodDays { get; set; }

    public int DaysCaptured { get; set; }
    public int DaysRequired { get; set; } = 30;
    public int PercentComplete { get; set; }
}

public class DashboardMetrics
{
    public DashboardMetric Steps { get; set; } = new();
    public DashboardMetric RestingHeartRate { get; set; } = new();
    public DashboardMetric Sleep { get; set; } = new();

    /// <summary>
    /// Nightly skin temperature, not core body temperature — wrist wearables don't measure the
    /// latter. <see cref="DashboardMetric.Baseline"/> is the wearer's own nightly baseline from
    /// the device (<c>ActivityLog.TemperatureBaseline</c>), not a CardiTrack-computed pattern
    /// baseline, so this stays meaningful during the 30-day learning window.
    /// </summary>
    public DashboardMetric Temperature { get; set; } = new();

    /// <summary>Blood oxygen saturation. No established-baseline comparison exists for this
    /// metric yet, so <see cref="DashboardMetric.Status"/> stays "unknown" — the value is shown
    /// without a trend judgement, against the published range in
    /// <see cref="DashboardMetric.Reference"/>.</summary>
    public DashboardMetric SpO2 { get; set; } = new();

    /// <summary>Breathing (respiratory) rate. Same no-established-baseline caveat as SpO2.</summary>
    public DashboardMetric BreathingRate { get; set; } = new();

    /// <summary>
    /// Overnight heart rate variability (RMSSD), in milliseconds. Compared against the member's own
    /// learned baseline and against nothing else: RMSSD is too personal for a published band to
    /// mean anything, so <see cref="DashboardMetric.Reference"/> stays empty here by design.
    /// </summary>
    public DashboardMetric HeartRateVariability { get; set; } = new();

    /// <summary>
    /// Breaths per minute averaged over the night, from the sleep-summary record. Kept beside
    /// <see cref="BreathingRate"/> rather than replacing it: the daily figure averages a whole day
    /// of stairs and naps, this one hours of stillness, and only this one has a learned baseline
    /// worth comparing against.
    /// </summary>
    public DashboardMetric OvernightBreathingRate { get; set; } = new();

}

public class DashboardMetric
{
    public decimal? Value { get; set; }
    public decimal? Baseline { get; set; }
    public decimal? ChangePercent { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    /// <summary>
    /// This member's own usual day from their <c>PatternBaseline</c> — not a target anyone set
    /// for them and not a published figure. Null until their own normal is known, because no
    /// standards body publishes a daily step count (see
    /// <see cref="CardiTrack.Application.Services.HealthReferenceRanges"/>) and a round number
    /// in its place would be ours wearing nobody's authority.
    /// </summary>
    /// <remarks>
    /// The dashboard Activity bar does <em>not</em> fill against this. It compares day n to the
    /// previous calendar day on <see cref="Series"/>, so a member still being learned still has
    /// a bar once two consecutive days exist. This field remains the usual-day figure for
    /// captions and explainers that talk about their own normal.
    /// </remarks>
    public decimal? Goal { get; set; }
    public int? RangeLow { get; set; }
    public int? RangeHigh { get; set; }

    /// <summary>
    /// 1-5 star rating of the reading against this member's own normal. What "normal" means is
    /// per metric: sleep takes the worse of the device's sleep efficiency and the night's duration
    /// against the sleep baseline, then caps that on the length of the night against both ends of
    /// the published recommendation, so hours slept well cannot rate above hours slept at all and a
    /// night far past the recommendation is not applauded either; temperature uses
    /// distance from its own nightly baseline in units of that device's nightly variation; steps
    /// and resting heart rate use percentage deviation from the pattern baseline (steps counting
    /// only a shortfall). See <c>MemberInsightsCalculator</c> for the bands. Null when there is
    /// nothing to rate against — SpO2 and breathing rate always; steps and resting heart rate
    /// when no baseline exists; and sleep only when the night carries neither an efficiency nor
    /// a baseline to compare its length with, since either alone can rate it. Null hides the
    /// card's star row rather than inventing a normal.
    /// </summary>
    public int? QualityScore { get; set; }

    /// <summary>
    /// One point per day, oldest first, always running to today — a day the member reported
    /// nothing for is a point with a null <see cref="MetricPoint.Value"/>, not a missing point.
    /// Its length is <c>MemberInsightsCalculator.SeriesDays</c>; clients showing a shorter window
    /// take the tail.
    /// </summary>
    public List<MetricPoint> Series { get; set; } = new();

    /// <summary>
    /// The published typical-adult range for this metric, for clients to draw behind the series
    /// alongside <see cref="Baseline"/> — this member's own normal against the wider population's.
    /// Null for metrics no standards body publishes a range for; see
    /// <see cref="CardiTrack.Application.Services.HealthReferenceRanges"/>.
    /// </summary>
    public MetricReference? Reference { get; set; }
}

/// <summary>
/// A published reference range — the population-level counterpart to
/// <see cref="DashboardMetric.Baseline"/>, which is the member's own learned normal.
/// </summary>
/// <remarks>
/// <para>
/// Presentational only: it is deliberately not an input to <see cref="DashboardMetric.Status"/> or
/// <see cref="DashboardMetric.QualityScore"/>, both of which stay relative to the member's own
/// baseline. CardiTrack is not a medical device, and a reading outside a population range is
/// context for a caregiver, not a finding.
/// </para>
/// <para>
/// The one exception is the sleep range, both ends of which cap the sleep
/// <see cref="DashboardMetric.QualityScore"/> — see
/// <c>MemberInsightsCalculator.CapAtRecommendedSleep</c>. It can only lower a rating the member's
/// own data already earned, because for sleep alone the member's own normal cannot be the whole
/// of the rating: a habitually short sleeper's baseline says their short nights are fine, and
/// no member-relative comparison reads a night as too long at all.
/// </para>
/// </remarks>
public class MetricReference
{
    public decimal Low { get; set; }
    public decimal High { get; set; }

    /// <summary>
    /// Who publishes the range ("WHO", "AHA", …) — shown next to it, because the ranges do not all
    /// come from one body and attributing them all to WHO would be wrong.
    /// </summary>
    public string Source { get; set; } = string.Empty;
}

public class MetricPoint
{
    public DateOnly Date { get; set; }
    public decimal? Value { get; set; }

    /// <summary>
    /// The day is still in progress, so this value is a running total rather than a finished one.
    /// Charts draw it apart from the completed days — plotting a half-finished step count on the
    /// same footing as whole ones is what made the alert detail's activity graph read as a further
    /// collapse when it was only lunchtime.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Services.AlertDetailComposer"/> sets this today, and only for step charts.
    /// A metric whose daily figure is settled by the time it is reported (last night's sleep) is
    /// not partial just because the calendar day it is filed under has not ended.
    /// </remarks>
    public bool IsPartial { get; set; }
}

public class DashboardAlertSummary
{
    public Guid AlertId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "yellow";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// new/acknowledged, the same lowercase vocabulary
    /// <see cref="AlertSummaryResponse.Status"/> uses — both come from
    /// <see cref="CardiTrack.Application.Services.AlertLifecycle"/>, so the dashboard strip and
    /// the alerts list can never describe one alert two ways.
    /// </summary>
    /// <remarks>
    /// "resolved" is absent by construction rather than by omission: this strip is what is going
    /// on now, and <see cref="DashboardResponse.RecentAlerts"/> only ever carries unresolved
    /// alerts. A resolved one is a closed episode, and it stays readable on the alerts list.
    /// </remarks>
    public string Status { get; set; } = "new";
}
