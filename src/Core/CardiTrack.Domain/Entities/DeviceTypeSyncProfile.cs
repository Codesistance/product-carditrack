using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// What we have learned about how one device type actually behaves: how long after a day ends its
/// data stops moving, how far back it revises, and how often a pull finds anything new. One row per
/// <see cref="Enums.DeviceType"/>.
/// </summary>
/// <remarks>
/// Purely observed output — the bounds a recommendation must respect live in configuration
/// (<c>DeviceProviders__&lt;i&gt;__Min/MaxPullIntervalMinutes</c>, set per environment from tfvars),
/// not here. Keeping them apart means a calibration run can never widen its own limits.
/// </remarks>
public class DeviceTypeSyncProfile : BaseEntity
{
    public DeviceType DeviceType { get; set; }

    // ── Observed ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hours from the end of a day until that day's values stop changing, at the median. Measured
    /// from DeviceActivityLog.UpdatedDate, which only moves when a provider's numbers actually
    /// change — see <c>DeviceActivityLogRepository.UpsertAsync</c>.
    /// </summary>
    public double? SettleLatencyP50Hours { get; set; }

    /// <summary>The same at the 95th percentile — what a pull schedule has to cover, not the median.</summary>
    public double? SettleLatencyP95Hours { get; set; }

    /// <summary>
    /// Age of the oldest day still seen to change, at the 99th percentile. Sets how far back a pull
    /// must reach. Only observable out to the audit pull's window: a provider that revises beyond it
    /// is invisible here, which is why the audit window is deliberately wider than the routine one.
    /// </summary>
    public double? RevisionTailP99Hours { get; set; }

    /// <summary>
    /// Fraction of pulls that changed anything, 0..1. Once webhook ingestion is live this doubles as
    /// the webhook miss rate — a scheduled pull that finds new data means a notification was missed
    /// or the provider revised history.
    /// </summary>
    public double? PollYieldRatio { get; set; }

    /// <summary>Device-days behind the figures above. A thin sample must not move anyone's cadence.</summary>
    public int SampleSize { get; set; }

    // ── Recommended ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Suggested interval between pulls, already clamped to the configured bounds for this device
    /// type. Null until a calibration run has enough data to say anything.
    /// </summary>
    public int? RecommendedPullIntervalMinutes { get; set; }

    /// <summary>Suggested trailing window, wide enough to cover <see cref="RevisionTailP99Hours"/>.</summary>
    public int? RecommendedLookbackDays { get; set; }

    /// <summary>
    /// When these figures were last recomputed. Distinct from <c>UpdatedDate</c>, which moves for any
    /// write; staleness here is what tells you a calibration run has stopped happening.
    /// </summary>
    public DateTime? CalculatedAt { get; set; }
}
