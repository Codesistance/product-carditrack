using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// How the readings inside one datapoint period are reduced to the single number the threshold is
/// compared against — CloudWatch's <c>Statistic</c>. A daily metric already <em>is</em> one number
/// per period, so only <see cref="Latest"/> is legal there; the rest apply to the minute series.
/// </summary>
public enum AlarmStatistic
{
    /// <summary>Lowest reading in the period.</summary>
    [Display(Name = "Minimum")]
    Minimum = 1,

    /// <summary>Highest reading in the period.</summary>
    [Display(Name = "Maximum")]
    Maximum = 2,

    /// <summary>Mean of the readings in the period. Ignores minutes with no sample — an unworn
    /// watch must never average in as a zero.</summary>
    [Display(Name = "Average")]
    Average = 3,

    /// <summary>Total across the period. Only meaningful for count-like metrics (steps, zone
    /// minutes); the catalogue refuses it on level-like ones, where a sum of heart rates is a
    /// number with no physical meaning.</summary>
    [Display(Name = "Total")]
    Sum = 4,

    /// <summary>The single value the period carries — the only statistic a daily metric offers,
    /// and on a minute series the last minute that reported.</summary>
    [Display(Name = "Value")]
    Latest = 5,
}
