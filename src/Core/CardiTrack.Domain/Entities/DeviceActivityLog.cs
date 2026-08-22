using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One day of metrics exactly as a single device reported them — the raw, per-device record.
/// <see cref="ActivityLog"/> is derived from these: a CardiMember wearing more than one device
/// has a row here per device per day, merged down to one ActivityLog row per day.
/// Kept separate so the merge stays recomputable and each value keeps its provenance.
/// </summary>
public class DeviceActivityLog : BaseEntity
{
    public Guid CardiMemberId { get; set; }
    public Guid DeviceConnectionId { get; set; }
    public DeviceType DataSource { get; set; }
    public DateOnly Date { get; set; }

    // Activity Metrics
    public int? Steps { get; set; }
    public decimal? Distance { get; set; } // in kilometers
    public int? ActiveMinutes { get; set; }
    public int? SedentaryMinutes { get; set; }
    public int? Floors { get; set; }
    public int? CaloriesBurned { get; set; }

    // Heart Rate Metrics
    public int? RestingHeartRate { get; set; }
    public int? AvgHeartRate { get; set; }
    public int? MaxHeartRate { get; set; }
    public int? MinHeartRate { get; set; }

    // Sleep Metrics
    public int? SleepMinutes { get; set; }
    public DateTime? SleepStartTime { get; set; }
    public DateTime? SleepEndTime { get; set; }
    public int? SleepEfficiency { get; set; } // 0-100 percentage
    public int? DeepSleepMinutes { get; set; }
    public int? LightSleepMinutes { get; set; }
    public int? RemSleepMinutes { get; set; }
    public int? AwakeMinutes { get; set; }

    // Additional Health Metrics
    public decimal? SpO2Average { get; set; }
    public decimal? SpO2Min { get; set; }
    public decimal? SpO2Max { get; set; }
    public decimal? VO2Max { get; set; }
    public int? StressScore { get; set; } // 0-100
    public decimal? BreathingRate { get; set; } // breaths per minute

    // Nightly skin temperature, not core body temperature — wrist wearables don't measure the
    // latter. Clinically meaningful only as a deviation from the wearer's own baseline, hence the
    // two companion columns below rather than this figure alone.
    public decimal? Temperature { get; set; }
    public decimal? TemperatureBaseline { get; set; } // the wearer's own nightly baseline
    public decimal? TemperatureVariation { get; set; } // relative nightly stddev over a 30-day window

    // Overnight readings, measured over hours of stillness rather than across the whole day —
    // null on a device that derives none, like every other optional reading above.
    public decimal? HeartRateVariabilityMs { get; set; } // RMSSD, milliseconds
    public decimal? OvernightBreathingRate { get; set; } // breaths per minute, asleep

    // Effort. Minutes in each of the wearer's own heart-rate zones, and the bpm their moderate
    // zone starts at — the device's Karvonen figure, not one CardiTrack derived. Null means the
    // day carries no zone rollup at all; a zone at 0 means they were measured and never reached it.
    public int? LightZoneMinutes { get; set; }
    public int? ModerateZoneMinutes { get; set; }
    public int? VigorousZoneMinutes { get; set; }
    public int? PeakZoneMinutes { get; set; }
    public int? ModerateZoneFloorBpm { get; set; }

    // Rest, as a shape rather than a total. SedentaryMinutes above says how much of the day was
    // still; this says how much of it was still *at once*, which is the part a family would want
    // to hear about.
    public int? LongestSedentaryStretchMinutes { get; set; }
    public DateTime? LongestSedentaryStretchStartUtc { get; set; }
}
