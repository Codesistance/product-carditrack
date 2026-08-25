namespace CardiTrack.Infrastructure.ExternalClients;

public interface IGoogleHealthApiClient
{
    Task<GoogleHealthActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date);
    Task<GoogleHealthHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date);
    Task<GoogleHealthSleepResult> GetSleepAsync(string accessToken, DateOnly date);
    Task<GoogleHealthAdditionalMetricsResult> GetAdditionalMetricsAsync(string accessToken, DateOnly date);
    /// <param name="sleepWindows">
    /// Every sleep session that ended on the day — the night and any nap — so the longest sedentary
    /// stretch is a waking-hours figure rather than the small hours or an afternoon nap. Null or
    /// empty returns no stretch at all rather than measuring the whole civil day: a figure that
    /// cannot be told from a night is worse than no figure — see the implementation's remarks. The
    /// zone readings are unaffected and are returned either way.
    /// </param>
    Task<GoogleHealthExertionResult> GetExertionAsync(
        string accessToken,
        DateOnly date,
        IReadOnlyCollection<(DateTime Start, DateTime End)>? sleepWindows = null);
}
