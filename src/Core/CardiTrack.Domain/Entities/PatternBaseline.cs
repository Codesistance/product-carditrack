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

    // Heart Rate Variability Baseline Metrics. Overnight RMSSD is strongly personal — a healthy
    // 80-year-old's normal can be a fifth of a healthy 40-year-old's — so it has no published band
    // and only the member's own window can say what is unusual for them. The robust pair is stored
    // alongside the mean for the same reason it is for steps and heart rate.
    public decimal? AvgHeartRateVariabilityMs { get; set; }
    public decimal? StdDevHeartRateVariability { get; set; }
    public decimal? MedianHeartRateVariabilityMs { get; set; }
    public decimal? MadHeartRateVariability { get; set; }

    // Overnight Breathing Baseline Metrics. The published adult band (12-20/min) is a wide one
    // and a rise inside it can still be this member's own change, which is why the rule that reads
    // this compares against them rather than against WHO.
    public decimal? AvgOvernightBreathingRate { get; set; }
    public decimal? StdDevOvernightBreathingRate { get; set; }

    // Effort and rest baselines. Elevated-zone minutes are the moderate, vigorous and peak zones
    // summed — the three that mean the heart was working — and the sedentary stretch is the
    // longest unbroken one, not the day's total.
    public int? AvgElevatedZoneMinutes { get; set; }
    public int? AvgLongestSedentaryStretchMinutes { get; set; }

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
