using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts;

/// <summary>
/// One statistical pattern worth telling a family about.
/// </summary>
/// <remarks>
/// Implementations must be pure over <see cref="AlertContext"/>. Rules must also be
/// <b>conservative</b>: the stated target is under 10% false positives, and an alert that turns out
/// to be nothing teaches a caregiver to ignore the next one — which is the failure this product
/// cannot recover from. When the data is thin or ambiguous, say nothing.
/// </remarks>
public interface IAlertRule
{
    AlertType AlertType { get; }
    AlertSeverity Severity { get; }

    AlertVerdict Evaluate(AlertContext context);
}

/// <summary>A rule's answer: silence, or an alert with the numbers that justified it.</summary>
public sealed record AlertVerdict
{
    public static readonly AlertVerdict None = new() { ShouldAlert = false };

    public bool ShouldAlert { get; private init; }

    /// <summary>Short headline shown in the list.</summary>
    public string Title { get; private init; } = string.Empty;

    /// <summary>Plain-language explanation. Never clinical advice — this is not a medical device.</summary>
    public string Message { get; private init; } = string.Empty;

    /// <summary>
    /// The measurements behind the decision, persisted to <c>Alert.MetricValues</c> so a caregiver
    /// (or a support engineer) can see why it fired rather than having to trust it.
    /// </summary>
    public IReadOnlyDictionary<string, object> MetricValues { get; private init; }
        = new Dictionary<string, object>();

    public static AlertVerdict Raise(
        string title, string message, IReadOnlyDictionary<string, object>? metricValues = null) => new()
        {
            ShouldAlert = true,
            Title = title,
            Message = message,
            MetricValues = metricValues ?? new Dictionary<string, object>()
        };
}
