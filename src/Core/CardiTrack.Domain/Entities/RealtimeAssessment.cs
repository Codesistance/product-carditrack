using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One real-time model assessment of a member's most recent hour of granular data: the SSA
/// features that went in, the model's narrative, and the severity that was routed. Derived
/// data — regenerable, never authoritative (docs/llm_design.md).
/// <para>
/// Composite-keyed (CardiMemberId, WindowStartUtc) and day-partitioned on
/// <see cref="WindowStartUtc"/>, the same shape as <see cref="GranularMetricHour"/>: the natural
/// key is also the idempotency key (the window only moves when new data lands, so an unchanged
/// window is never assessed twice), and retention is a partition drop.
/// </para>
/// </summary>
public class RealtimeAssessment
{
    public Guid CardiMemberId { get; set; }

    /// <summary>First minute of the assessed 60-minute window (UTC). The partition key.</summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>Exclusive end of the assessed window (UTC).</summary>
    public DateTime WindowEndUtc { get; set; }

    /// <summary>The SSA trend's final value — the denoised heart rate at the window's end, bpm.</summary>
    public double HrTrendLast { get; set; }

    /// <summary>
    /// |last observed − trend| / noise RMS: how many "typical jitters" the latest reading sits
    /// from the denoised trend. The model receives this as its deviation yardstick. The
    /// denominator is floored (a near-constant window would otherwise divide by ~zero), so this
    /// is not always exactly reconstructable from <see cref="HrNoiseRms"/>.
    /// </summary>
    public double HrDeviationScore { get; set; }

    /// <summary>
    /// RMS of the SSA noise residual, bpm — what one typical jitter is for this member, stored
    /// as measured (unfloored): a genuinely quiet signal must stay distinguishable from one
    /// sitting at the deviation floor.
    /// </summary>
    public double HrNoiseRms { get; set; }

    /// <summary>Total steps observed in the window, when any step data was present.</summary>
    public double? StepsSum { get; set; }

    /// <summary>Mean SpO₂ over the window's sparse samples, %, when any were present.</summary>
    public double? SpO2Mean { get; set; }

    /// <summary>The model's assessment text, verbatim.</summary>
    public string ModelOutput { get; set; } = string.Empty;

    /// <summary>
    /// The severity word the model actually emitted (its own vocabulary), kept for audit even
    /// when it fails to parse; null when the output carried no recognisable severity line.
    /// </summary>
    public string? RawSeverity { get; set; }

    /// <summary>
    /// The routed severity — <see cref="RawSeverity"/> mapped onto the product taxonomy — or
    /// null when the model's output could not be parsed. A null severity never raises an alert:
    /// an unparseable answer is treated as no answer, not as an emergency.
    /// </summary>
    public AlertSeverity? Severity { get; set; }

    /// <summary>
    /// Named SSA numerical engine that produced the stored features (today
    /// <c>MathNet.Numerics.Evd</c>). Null on rows written before this column existed.
    /// Needed so a later solver swap does not mix engines inside one 90-day partition.
    /// </summary>
    public string? SsaEngine { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
