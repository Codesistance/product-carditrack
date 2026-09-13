namespace CardiTrack.Infrastructure.ExternalClients;

public interface IGoogleHealthApiClient
{
    Task<GoogleHealthActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date);
    Task<GoogleHealthHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date);
    Task<GoogleHealthSleepResult> GetSleepAsync(string accessToken, DateOnly date);
    Task<GoogleHealthAdditionalMetricsResult> GetAdditionalMetricsAsync(string accessToken, DateOnly date);
    /// <param name="sleepWindows">
    /// Every sleep session that overlaps the day — the night that ended on it, any nap, and the
    /// night that <em>starts</em> on it — so the longest sedentary stretch is a waking-hours
    /// figure rather than the small hours, an afternoon nap, or the bedtime-to-midnight tail.
    /// Null or empty returns no stretch at all rather than measuring the whole civil day: a
    /// figure that cannot be told from a night is worse than no figure — see the implementation's
    /// remarks. The zone readings are unaffected and are returned either way.
    /// </param>
    Task<GoogleHealthExertionResult> GetExertionAsync(
        string accessToken,
        DateOnly date,
        IReadOnlyCollection<(DateTime Start, DateTime End)>? sleepWindows = null);
}
