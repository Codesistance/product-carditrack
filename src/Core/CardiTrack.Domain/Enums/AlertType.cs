using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

public enum AlertType
{
    [Display(Name = "Inactivity")]
    Inactivity = 1,

    [Display(Name = "Heart Rate")]
    HeartRate = 2,

    [Display(Name = "Sleep")]
    Sleep = 3,

    [Display(Name = "Pattern Break")]
    PatternBreak = 4,

    [Display(Name = "Trend")]
    Trend = 5,

    /// <summary>
    /// A rhythm finding the wearable itself made — an irregular-rhythm notification, or an ECG the
    /// device classified as atrial fibrillation. Deliberately not <see cref="HeartRate"/>: that
    /// type is cooldown-scoped as a whole (see <c>AlertRuleMarkers.Suppresses</c>), so a standing
    /// "resting heart rate is running high" would have swallowed the most consequential alert in
    /// the product. The remedy differs too — a rate alert asks the family to check in, a rhythm
    /// finding asks them to get it in front of a doctor.
    /// </summary>
    [Display(Name = "Rhythm")]
    Rhythm = 6
}
