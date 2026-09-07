namespace CardiTrack.Application.Services;

/// <summary>
/// The readings a status-routed question can name. Order is the one
/// <see cref="ChatDataRegistry.PrimaryMetricNamed"/> walks, so compound names win: "heart rate
/// variability" is not a heart-rate caption, and "overnight breathing" is not sleep.
/// </summary>
public enum StatusMetric
{
    HeartRateVariability = 1,
    RestingHeartRate = 2,
    Oxygen = 3,
    BreathingRate = 4,
    Sleep = 5,
    Steps = 6,
}
