namespace CardiTrack.Domain.Entities;

/// <summary>
/// One analysis window from an irregular-rhythm notification, with the beats the device measured
/// inside it. The only beat-level cardiac data CardiTrack holds: every other heart-rate store in
/// the product is a one-minute average or coarser.
/// <para>
/// Composite-keyed (CardiMemberId, DeviceConnectionId, WindowStartUtc) and day-partitioned on
/// <see cref="WindowStartUtc"/>, kept for the same 90 days as <see cref="RealtimeAssessment"/>.
/// The natural key is also the idempotency key: the routine window re-reads the last three days on
/// every pull, so the same window arrives repeatedly and must land once. It carries the connection
/// because these rows are per device, like <see cref="GranularMetricHour"/> — two watches on one
/// wearer can cover the same minutes, and each window is a measurement one of them made.
/// </para>
/// </summary>
/// <remarks>
/// <b>This is evidence, not a detection substrate.</b> Google only retains and serves the beats
/// inside windows its own AFib algorithm already scored positive — `AlertWindow` in the v4
/// discovery document says so outright, and that no negative or inconclusive window is ever saved.
/// So these rows arrive only after a finding has been made, and nothing here can be used to make
/// one independently: a rule that treated an absence of rows as an absence of arrhythmia would be
/// reading silence as reassurance. What the beats are good for is telling a caregiver — and the
/// clinician they take it to — how long the episode ran and how irregular it looked, instead of
/// only that a notification happened.
/// </remarks>
public class RhythmEpisode
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Start of the analysis window (UTC). The partition key.</summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>End of the analysis window (UTC).</summary>
    public DateTime WindowEndUtc { get; set; }

    /// <summary>The connection that measured this window. Part of the key — see the class remarks.</summary>
    public Guid DeviceConnectionId { get; set; }

    /// <summary>
    /// Start of the notification this window belonged to. One notification carries several
    /// overlapping windows, and a caregiver was told about the notification, not the windows —
    /// so this is what groups rows back into the one thing that actually happened.
    /// </summary>
    public DateTime NotificationStartUtc { get; set; }

    /// <summary>
    /// The window's own AFib verdict, as served. Expected to be <c>true</c> on every row: the
    /// API's current algorithm emits a notification only when all its windows are positive, and
    /// never persists the negative ones. Stored faithfully rather than assumed, but no rule should
    /// branch on it — a filter that has never once excluded a row is a filter nobody is testing.
    /// </summary>
    public bool Positive { get; set; }

    /// <summary>Beats in <see cref="RrMilliseconds"/> — a cheap emptiness check.</summary>
    public int BeatCount { get; set; }

    /// <summary>
    /// Interbeat intervals in milliseconds, one per beat, in observation order. Derived as
    /// <c>60000 / beatsPerMinute</c>, which the v4 schema documents as exactly how that field was
    /// computed from the underlying RR gap — so this recovers the measurement rather than
    /// re-deriving it from timestamps.
    /// </summary>
    public int[] RrMilliseconds { get; set; } = [];

    /// <summary>
    /// Each beat's offset from <see cref="WindowStartUtc"/>, milliseconds. Kept alongside
    /// <see cref="RrMilliseconds"/> rather than derived from it because the two are independent
    /// measurements: the offsets come from each beat's own <c>physicalTime</c>, the intervals from
    /// its <c>beatsPerMinute</c>. Where beats are missing the offsets show the gap and the
    /// intervals cannot.
    /// </summary>
    public int[] OffsetMillisFromStart { get; set; } = [];

    public int MeanRrMs { get; set; }

    public int MinRrMs { get; set; }

    public int MaxRrMs { get; set; }

    /// <summary>
    /// Root mean square of successive RR differences, milliseconds — the standard beat-to-beat
    /// irregularity statistic, and the one figure here a clinician will look for first. Computed
    /// over <see cref="RrMilliseconds"/> at ingestion; null on a window of fewer than two beats,
    /// where successive differences do not exist.
    /// </summary>
    public int? RmssdMs { get; set; }

    public DateTime IngestedAtUtc { get; set; }
}
