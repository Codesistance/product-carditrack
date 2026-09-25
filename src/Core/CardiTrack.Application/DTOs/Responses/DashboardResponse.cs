using CardiTrack.Domain.Enums;

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
    /// Shown under the name on the dashboard's hero card, beside the age. Carried here rather
    /// than read from <c>CardiMemberDetailResponse</c> because the dashboard never fetches that
    /// payload: the hero card is the first thing a caregiver sees, and it should not need a
    /// second round trip to say who the member is.
    /// </summary>
    public Gender Gender { get; set; }

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
    /// Every alert on this member that nobody has closed — new and acknowledged alike, the same
    /// unresolved set <see cref="HealthStatus"/> is coloured from. Distinct from
    /// <see cref="UnreadAlertCount"/>, which is the unacknowledged part of it: the CardiMember
    /// card's Alerts glyph reads that narrower set, because acknowledging is the caregiver
    /// answering the request for attention even though the episode stays open.
    /// </summary>
    public int OpenAlertCount { get; set; }

    /// <summary>
    /// Whether this member has a current wellness suggestion on the CardiMember Details Tip card
    /// (<c>GET api/v1/insights/members/{id}/advise</c>). A plain existence-and-freshness check
    /// against the persisted <c>MemberAdvise</c> row — same staleness ceiling as the read endpoint
    /// (<c>AdviseStaleness.MaxAge</c>, shared so the two can't drift) — never a model call, so the
    /// Dashboard card's pulse indicator costs nothing beyond what this response already pays for.
    /// </summary>
    public bool HasAdvise { get; set; }

    /// <summary>
    /// When the suggestion behind <see cref="HasAdvise"/> was generated; null when there is none.
    /// The CardiMember card compares it with the moment the caregiver last opened the suggestion
    /// to tell a fresh one from one already read — the row itself carries no read state, and a
    /// regeneration is what makes an old suggestion new again.
    /// </summary>
    public DateTime? AdviseGeneratedAt { get; set; }

    /// <summary>
    /// When this member's most recent CardiJournal entry (the family digest) was generated; null
    /// while none has been. Read the same way as <see cref="AdviseGeneratedAt"/>: the card pulses
    /// its CardiJournal button while there is an entry to read and colours the glyph until the
    /// caregiver has opened one at least this new.
    /// </summary>
    public DateTime? LatestJournalEntryAt { get; set; }
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

    /// <summary>
    /// How this member reads against their own learned normal, and — once they have a month of
    /// history — which way they have been going. Null while nothing has been written for them yet,
    /// or while what was written has gone stale; the card renders nothing rather than a heading
    /// over an empty body.
    /// </summary>
    public MemberInsightResponse? Insight { get; set; }

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
    /// metric yet, so inside the published range in <see cref="DashboardMetric.Reference"/>
    /// <see cref="DashboardMetric.Status"/> stays "unknown" — there is no usual to judge it
    /// against. Outside the range it is "yellow": the range is what normal means for blood
    /// oxygen.</summary>
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
    /// One point per day, oldest first, running to today — or, on a member detail asked for
    /// with <c>seriesEndsOn</c> naming a past day, to that day, while the rest of the metric
    /// stays about today. A <c>seriesEndsOn</c> of today or later, or one too early to have a
    /// full window before it, gives today's series. A day the member reported nothing for is a
    /// point with a null <see cref="MetricPoint.Value"/>, not a missing point. Its length is
    /// <c>MemberInsightsCalculator.SeriesDays</c>; clients showing a shorter window take the tail.
    /// </summary>
    public List<MetricPoint> Series { get; set; } = new();

    /// <summary>
    /// The published typical-adult range for this metric, for clients to draw behind the series
    /// alongside <see cref="Baseline"/>. For sleep, resting heart rate and blood oxygen it is what
    /// normal means, and <see cref="Baseline"/> is secondary context — whether a reading is new
    /// for this member. Null for metrics no standards body publishes a range for; see
    /// <see cref="CardiTrack.Application.Services.HealthReferenceRanges"/>.
    /// </summary>
    public MetricReference? Reference { get; set; }

    /// <summary>
    /// Sleep only: what is known about the night <see cref="Value"/> is from. An
    /// <see cref="NightSleepStatus.Awake"/> night — the watch worn all night and no sleep
    /// recorded — carries a <see cref="Value"/> of 0, and clients should say so rather than print
    /// the 0. Null on every other metric, and on sleep when there is no reading or it predates
    /// the classification.
    /// </summary>
    /// <remarks>
    /// This is the night of the newest <em>reading</em>, which is not always last night: while last
    /// night is still <see cref="NightSleepStatus.Pending"/> the card shows the night before. The
    /// newest point of <see cref="Series"/> says whether last night has arrived.
    /// </remarks>
    public NightSleepStatus? NightStatus { get; set; }
}

/// <summary>
/// A published reference range — the population-level counterpart to
/// <see cref="DashboardMetric.Baseline"/>, which is the member's own learned normal.
/// </summary>
/// <remarks>
/// <para>
/// For sleep, resting heart rate and blood oxygen the range is what normal means (decision
/// 2026-09-25): a reading outside it makes <see cref="DashboardMetric.Status"/> at least "yellow",
/// even when it is the member's own usual, and inside it the status is judged against their
/// baseline. For every other metric it is background for the chart and no input to either field.
/// Outside a range is still worth a look rather than a finding — CardiTrack is not a medical
/// device.
/// </para>
/// <para>
/// The sleep range also caps the sleep <see cref="DashboardMetric.QualityScore"/> at both ends — see
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

    /// <summary>
    /// True when this range is what normal means for the metric — sleep, resting heart rate and
    /// blood oxygen (decision 2026-09-25) — rather than background for the chart. A client draws
    /// such a range as the primary reference and the member's usual as the secondary one, and
    /// the server colours <see cref="DashboardMetric.Status"/> from it. False for the waking
    /// breathing band (WHO's 12–20 is a rate at rest, not a normal for anything this app
    /// measures overnight). Set once, in <see cref="Services.HealthReferenceRanges"/>, so every
    /// route a range travels by carries the same answer.
    /// </summary>
    public bool IsPublishedNormal { get; set; }
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

    /// <summary>
    /// Sleep series only: what is known about this day's night. Tells apart the three things a
    /// null or zero <see cref="Value"/> can mean — <see cref="NightSleepStatus.Awake"/> (a 0 that
    /// is a real night of no sleep), <see cref="NightSleepStatus.Pending"/> (not arrived yet, may
    /// still come) and <see cref="NightSleepStatus.NoData"/> (nothing reached us). Null on every
    /// other metric's points, on days with no row, and on nights recorded before the status
    /// existed.
    /// </summary>
    public NightSleepStatus? NightStatus { get; set; }
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
    /// The same lowercase vocabulary <see cref="AlertSummaryResponse.Status"/> uses — both come
    /// from <see cref="CardiTrack.Application.Services.AlertLifecycle"/>, so the dashboard strip
    /// and the alerts list can never describe one alert two ways.
    /// </summary>
    /// <remarks>
    /// Always "new" today, by construction rather than by omission: this strip is what still
    /// wants the caregiver's attention, and <see cref="DashboardResponse.RecentAlerts"/> only
    /// carries alerts that are neither acknowledged nor resolved. Both of the other two are
    /// answers someone has already given — one says it has been seen, the other that the episode
    /// is over — and both stay readable on the alerts list. Kept as a field rather than dropped
    /// so a client never has to infer the lifecycle from the strip it happens to be reading.
    /// </remarks>
    public string Status { get; set; } = "new";
}

/// <summary>
/// One metric that moved, graded, with everything a client needs to draw it and to offer an alarm
/// on it.
/// </summary>
/// <remarks>
/// <see cref="Headline"/> and <see cref="Basis"/> are rendered server-side on purpose. The figures
/// are rounded and worded in one place — <c>BaselineMovementCalculator</c> — so the block the
/// model was given and the card a caregiver reads cannot describe the same movement two ways, and
/// the grounds a grade was given on travel with the grade rather than being reconstructed by
/// whichever client is drawing it.
/// </remarks>
public class MemberMovementResponse
{
    /// <summary>The metric's stable key, for a client deciding something about it.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>The label a caregiver reads.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The sentence: where it sits now, against what is usual for them.</summary>
    public string Headline { get; set; } = string.Empty;

    /// <summary>
    /// How to draw it: "favourable", "neutral" or "attention". About the movement, never about
    /// the person — see <c>MovementValence</c>.
    /// </summary>
    public string Valence { get; set; } = string.Empty;

    /// <summary>
    /// What the grade was given on, in a caregiver's words — a published range and the body that
    /// publishes it, or a plain statement that this is their own usual and why no published range
    /// applies. Never empty: a grade with no stated grounds is an opinion wearing a tick.
    /// </summary>
    public string Basis { get; set; } = string.Empty;

    /// <summary>What the two figures are in.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The recent average.</summary>
    public decimal Recent { get; set; }

    /// <summary>Their own learned usual.</summary>
    public decimal Usual { get; set; }

    /// <summary>Signed whole percent, negative below their usual.</summary>
    public decimal DeviationPercent { get; set; }

    /// <summary>
    /// The alarm metric that watches this reading, or null where none does — active minutes has
    /// no alarm, and offering the nearest one would set a caregiver watching a different figure
    /// from the one they were shown.
    /// </summary>
    public string? AlarmMetric { get; set; }

    /// <summary>
    /// A starting share of their usual for an alarm on this, or null where there is no alarm to
    /// offer. A suggestion to be edited before saving, derived from this member's own departure
    /// rather than from any clinical level.
    /// </summary>
    public decimal? SuggestedThresholdPercent { get; set; }
}

/// <summary>
/// The stored interpretations a caregiver sees beside the numbers: the reading against their own
/// baseline, and the longer trend narrative where one exists.
/// </summary>
/// <remarks>
/// Both are written by pipeline passes and served from rows, never generated on the request — see
/// <c>MemberInsight</c>. They are carried together because a caregiver reads them together: the
/// baseline half answers "how are they now", the trend half "where has this been going", and
/// either alone invites the reader to supply the other from imagination.
/// </remarks>
public class MemberInsightResponse
{
    /// <summary>The reading against their own baseline. Null when none is stored or it is stale.</summary>
    public string? Summary { get; set; }

    /// <summary>The supporting points behind <see cref="Summary"/>, in order.</summary>
    public IReadOnlyList<string> KeyFindings { get; set; } = [];

    /// <summary>
    /// The measured movements behind <see cref="KeyFindings"/>, widest departure first — what a
    /// client draws one card per, and offers an alarm on. Empty where nothing moved.
    /// </summary>
    /// <remarks>
    /// Carried beside the sentences rather than instead of them. The figures here are the same
    /// ones the model was given to write <see cref="Summary"/> from, so a client showing both is
    /// showing one set of numbers described two ways, not two readings.
    /// </remarks>
    public IReadOnlyList<MemberMovementResponse> Movements { get; set; } = [];

    /// <summary>
    /// The trend narrative. Null while the member has under a month of readings — that is the
    /// learning state, and there is no trajectory to describe yet.
    /// </summary>
    public string? Trend { get; set; }

    /// <summary>The supporting points behind <see cref="Trend"/>, in order.</summary>
    public IReadOnlyList<string> TrendFindings { get; set; } = [];

    /// <summary>
    /// True while no baseline exists at all — the state the dashboard already calls "getting to
    /// know you". Served from the stored row so the two surfaces cannot disagree.
    /// </summary>
    public bool IsLearning { get; set; }

    /// <summary>True when the only baseline behind <see cref="Summary"/> was a short window.</summary>
    public bool IsProvisional { get; set; }

    /// <summary>The window the baseline behind <see cref="Summary"/> covered, in days.</summary>
    public int? BaselinePeriodDays { get; set; }

    /// <summary>When the newer of the two rows was written.</summary>
    public DateTime? GeneratedAt { get; set; }
}
