namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// The values <see cref="AlertDetailResponse.Reason"/> takes. Named constants rather than an enum
/// because both ends of the wire are string-matching them — the mobile app switches on the value
/// to pick an icon, and an unrecognised one has to fall back rather than fail to deserialise.
/// </summary>
public static class AlertReasons
{
    public const string Activity = "activity";
    public const string Heart = "heart";
    public const string Sleep = "sleep";
    public const string Device = "device";

    /// <summary>The catch-all: something is off with this member's pattern, unattributed.</summary>
    public const string Monitoring = "monitoring";
}

/// <summary>
/// One alert for the mobile detail screen (M1-11 / M1-12 / M1-16). The chart, when present, is
/// the single series that caused the alert — never the dashboard's six-metric payload.
/// </summary>
public class AlertDetailResponse
{
    public Guid AlertId { get; set; }
    public Guid CardiMemberId { get; set; }
    public string CardiMemberName { get; set; } = string.Empty;
    public string? CardiMemberPhotoUrl { get; set; }
    public string? Phone { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactName { get; set; }

    /// <summary>AlertType display name — "Inactivity", "Heart Rate", …</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// What the alert is about, as a stable key the mobile app maps to an icon: <c>activity</c>,
    /// <c>heart</c>, <c>sleep</c>, <c>device</c> or <c>monitoring</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not the severity. Severity is already carried by the banner's colour, so an
    /// icon spending itself on the same fact tells the caregiver nothing the screen had not
    /// already said — and the one thing the banner cannot say in colour is what kind of alert this
    /// is. Distinct from <see cref="Rule"/>, which is the producer's own stamp and too fine-grained
    /// to bind an icon to (five rules share three icons, and a new rule must not mean a new asset).
    /// </remarks>
    public string Reason { get; set; } = AlertReasons.Monitoring;

    /// <summary>
    /// The producer stamp in <c>MetricValues</c> (<c>activity_decline</c>, <c>realtime_hr</c>, …),
    /// or null when the row predates rule markers.
    /// </summary>
    public string? Rule { get; set; }

    /// <summary>green/yellow/orange/red.</summary>
    public string Severity { get; set; } = "yellow";

    /// <summary>new/acknowledged/resolved.</summary>
    public string Status { get; set; } = "new";

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// The civil day the alert is about. Daily-grain rules that judge yesterday
    /// (<c>activity_decline</c>, <c>elevated_heart_rate</c>, <c>long_term_trend</c>,
    /// <c>daytime_inactivity_block</c>, <c>elevated_zone_without_movement</c>) stamp that
    /// day; sleep stamps the night it judged. The banner date follows this, not
    /// <see cref="TriggeredAt"/>, so a quieter day is not dated as the afternoon we noticed it.
    /// </summary>
    public DateOnly AboutDate { get; set; }

    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public string? AcknowledgedByName { get; set; }

    /// <summary>The two-column "current vs usual" block, or null when the rule has no scalars.</summary>
    public AlertComparisonResponse? Comparison { get; set; }

    /// <summary>
    /// Why this alert fired — the producer's own yardstick, in the caregiver's register. Null on a
    /// row this build cannot explain honestly (a markerless legacy alert, or a rule it does not
    /// know), because a card that shrugs still reads as a claim.
    /// </summary>
    public AlertEvidenceResponse? Evidence { get; set; }

    /// <summary>
    /// The one series this alert is about. Null when the rule has no health graph
    /// (<c>device_silence</c>) or there is nothing to plot.
    /// </summary>
    public AlertChartResponse? Chart { get; set; }

    /// <summary>Last day with measured steps, for no-morning / silence copy. Null when unknown.</summary>
    public DateOnly? LastActivityOn { get; set; }

    /// <summary>Typical wake time ("07:00") for the no-morning rule.</summary>
    public string? TypicalWakeTime { get; set; }

    /// <summary>
    /// Typical bedtime ("22:00") from the baseline, already on the member's wall clock.
    /// Null when the member has no learned bedtime — the screen then treats 20:00 as evening.
    /// </summary>
    public string? TypicalBedtime { get; set; }

    /// <summary>
    /// The still-stretch ask, composed in the member's zone so a caregiver's phone clock
    /// cannot flip "chair" and "settled early". Null when the rule has no stretch start.
    /// </summary>
    public string? StillStretchAsk { get; set; }

    /// <summary>
    /// When the still stretch began, on the member's wall clock ("2:00 PM"). The ask sits
    /// on this same clock; the caregiver's phone must not re-derive it.
    /// </summary>
    public string? StretchStartedLabel { get; set; }

    /// <summary>When the device last produced a reading, for <c>device_silence</c>.</summary>
    public DateTime? LastDataAt { get; set; }

    /// <summary>
    /// When the still stretch began (UTC), for <c>daytime_inactivity_block</c>. The rule's own copy
    /// deliberately names no clock time — this is the field the detail screen turns into one, so a
    /// caregiver can tell an afternoon in a chair from an episode that needs a second look. Null on
    /// other rules and on rows raised before the instant was stored.
    /// </summary>
    public DateTime? StretchStartedAt { get; set; }
}

public class AlertComparisonResponse
{
    public string CurrentLabel { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string NormalLabel { get; set; } = string.Empty;
    public string NormalValue { get; set; } = string.Empty;
    public string? ChangeLabel { get; set; }

    /// <summary>
    /// The same deviation <see cref="ChangeLabel"/> describes, signed and rounded to whole
    /// percent — negative below normal, positive above, zero in line. Null when there is nothing
    /// to compare.
    /// </summary>
    /// <remarks>
    /// The label already carries "below"/"above" in words, so this exists for what words cannot
    /// drive: which way the arrow beside it points. Deriving that by parsing the sentence would
    /// make a display decision depend on its wording.
    /// </remarks>
    public decimal? ChangePercent { get; set; }
}

/// <summary>
/// One metric's window for the detail chart. <see cref="Series"/> is oldest-first; a missing
/// day (or minute) is a point with a null value, not a hole in the list.
/// </summary>
public class AlertChartResponse
{
    /// <summary>steps / restingHeartRate / sleep / heartRate.</summary>
    public string Metric { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;

    /// <summary>Caption under the name — "Last 14 days", "This hour".</summary>
    public string WindowLabel { get; set; } = string.Empty;

    /// <summary>
    /// The headline figure — always a <em>finished</em> day. Where the window runs up to the day
    /// in progress, that day is reported by <see cref="PartialDayLabel"/> instead, never here.
    /// </summary>
    public decimal? Value { get; set; }

    /// <summary>
    /// Which day <see cref="Value"/> belongs to ("Yesterday", "10 Aug"), so the number in the
    /// chart header, the comparison card and the flagged series point are visibly the same
    /// reading. Set when the headline is the about-day or the window includes a day in
    /// progress; null when the headline is simply the latest settled slot.
    /// </summary>
    public string? ValueLabel { get; set; }

    public decimal? Baseline { get; set; }

    /// <summary>
    /// The published typical-adult band for this metric, shaded behind the line — the same
    /// <see cref="DashboardMetric.Reference"/> the dashboard's trend card draws. Null for a metric
    /// no standards body publishes a range for.
    /// </summary>
    /// <remarks>
    /// <see cref="Baseline"/> alone is the member's own usual, and on its own it cannot answer the
    /// question a sleep alert raises: a night plotted against a dashed 3.8 says nothing about
    /// whether 3.8 is anywhere near enough. The band is the only thing on the chart that is not
    /// relative to the member, which is what makes "well off the usual" readable as better or
    /// worse rather than merely different.
    /// </remarks>
    public MetricReference? Reference { get; set; }

    public List<MetricPoint> Series { get; set; } = new();

    /// <summary>
    /// The day in progress, measured against the same stretch of the day before — "865 steps so
    /// far today, 21% below the 1,102 by this time yesterday".
    /// </summary>
    /// <remarks>
    /// A whole day against a part of one is not a comparison, and it is the comparison a caregiver
    /// makes on sight when a running total is plotted next to finished days. This sentence is the
    /// like-for-like one: both figures cover the same number of elapsed minutes since local
    /// midnight, summed from the minute-grain store. Null whenever that store cannot answer for
    /// either stretch — an unfair comparison is worse than none.
    /// </remarks>
    public string? PartialDayLabel { get; set; }
}

/// <summary>
/// The evidence behind one alert: what the rule watched, and where its line sat. Composed in .NET
/// from the figures the producer stamped — never a model call, so there is nothing here that was
/// not either measured or written by hand.
/// </summary>
/// <seealso cref="CardiTrack.Application.Services.AlertEvidenceComposer"/>
public class AlertEvidenceResponse
{
    /// <summary>
    /// The rule's name on the alert-settings screen ("Heart rate variability has dropped"), or the
    /// caregiver's own name for their alarm. The card a caregiver is reading and the toggle that
    /// would turn it off have to be visibly the same thing.
    /// </summary>
    public string RuleLabel { get; set; } = string.Empty;

    /// <summary>
    /// One or two sentences saying what it took to raise this — the yardstick, and why the rule is
    /// shaped that way where the shape is the surprising part (both halves of a pairing, two nights
    /// rather than one, a measured zero rather than a missing reading).
    /// </summary>
    public string WhyLine { get; set; } = string.Empty;

    /// <summary>
    /// The line this reading crossed, as a short phrase for a chip beside the comparison — "Below
    /// 47.6 ms, two nights running". Null when the producer stamped no margin and the rule's
    /// constant alone cannot name a figure this member's reading was actually judged against.
    /// </summary>
    public string? ThresholdLabel { get; set; }

    /// <summary>
    /// The window the "usual" in <see cref="WhyLine"/> was learned over, in days. Null when no
    /// baseline was read — the figures then came from the alert's own stamp, and naming a window
    /// we did not look at would be an invention.
    /// </summary>
    public int? BaselinePeriodDays { get; set; }
}
