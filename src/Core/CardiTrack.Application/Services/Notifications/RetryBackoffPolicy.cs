namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Backoff schedule for a <c>Retryable</c> send outcome — 15s, 60s, 5m, 30m, 2h, then dead-letter
/// (notification_engine.md §6.2). Pure lookup by attempt count.
/// </summary>
public static class RetryBackoffPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2)
    ];

    /// <summary>Null once <paramref name="attempts"/> exhausts the schedule — the caller dead-letters instead of retrying again.</summary>
    public static TimeSpan? NextDelay(int attempts) =>
        attempts >= 0 && attempts < Delays.Length ? Delays[attempts] : null;

    public static bool IsExhausted(int attempts) => attempts >= Delays.Length;
}
