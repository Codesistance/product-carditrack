namespace CardiTrack.Infrastructure.ExternalClients;

public interface IGoogleHealthApiClient
{
    Task<GoogleHealthActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date);
    Task<GoogleHealthHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date);
    Task<GoogleHealthSleepResult> GetSleepAsync(string accessToken, DateOnly date);
    Task<GoogleHealthAdditionalMetricsResult> GetAdditionalMetricsAsync(string accessToken, DateOnly date);
    /// <param name="sleepWindow">
    /// The night's sleep session, so the longest sedentary stretch is a daytime figure rather than
    /// the small hours. Null measures the whole civil day — see the implementation's remarks.
    /// </param>
    Task<GoogleHealthExertionResult> GetExertionAsync(
        string accessToken, DateOnly date, (DateTime Start, DateTime End)? sleepWindow = null);
}
