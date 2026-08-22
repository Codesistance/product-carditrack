using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

public class ActivityLog : BaseEntity
{
    public Guid CardiMemberId { get; set; }
    public Guid DeviceConnectionId { get; set; }
    public DeviceType DataSource { get; set; } // Which device provided this data
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
    public decimal? SpO2Average { get; set; } // Blood oxygen saturation
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

    // Overnight heart rate variability (RMSSD), from the wearable itself — null on a device that
    // derives none, like every other optional reading above.
    public decimal? HeartRateVariabilityMs { get; set; }
}
