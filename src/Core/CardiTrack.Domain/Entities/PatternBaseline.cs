using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

public class PatternBaseline : BaseEntity
{
    public Guid CardiMemberId { get; set; }
    public DateTime CalculatedDate { get; set; }
    public int PeriodDays { get; set; } // 30, 60, or 90 days

    // Activity Baseline Metrics
    public int? AvgSteps { get; set; }
    public decimal? StdDevSteps { get; set; }
    /// <summary>Robust location for steps. Stored alongside the mean; R1 alerts still use <see cref="AvgSteps"/>.</summary>
    public int? MedianSteps { get; set; }
    /// <summary>Unscaled MAD (median of |x − median|). Not used for live alerts — see G2 shadow eval.</summary>
    public decimal? MadSteps { get; set; }
    public int? AvgActiveMinutes { get; set; }

    // Heart Rate Baseline Metrics
    public int? AvgRestingHeartRate { get; set; }
    public decimal? StdDevHeartRate { get; set; }
    public int? MedianRestingHeartRate { get; set; }
    public decimal? MadHeartRate { get; set; }
    public int? MaxHeartRateObserved { get; set; }

    // Sleep Baseline Metrics
    public int? AvgSleepMinutes { get; set; }
    public int? MedianSleepMinutes { get; set; }
    public decimal? MadSleepMinutes { get; set; }
    public TimeOnly? TypicalBedtime { get; set; }
    public TimeOnly? TypicalWakeTime { get; set; }
    public int? AvgSleepEfficiency { get; set; }

    // JSON: [5200, 4800, 5100, 5300, 5400, 4900, 3200] (Mon-Sun)
    public string? StepsByDayOfWeek { get; set; }

    public PatternBaseline()
    {
        CalculatedDate = DateTime.UtcNow;
    }
}
