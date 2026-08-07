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
    public decimal? Temperature { get; set; } // in Celsius
}
