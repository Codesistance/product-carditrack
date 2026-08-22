namespace CardiTrack.Infrastructure.ExternalClients;

public interface IGoogleHealthApiClient
{
    Task<GoogleHealthActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date);
    Task<GoogleHealthHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date);
    Task<GoogleHealthSleepResult> GetSleepAsync(string accessToken, DateOnly date);
    Task<GoogleHealthAdditionalMetricsResult> GetAdditionalMetricsAsync(string accessToken, DateOnly date);
    Task<GoogleHealthExertionResult> GetExertionAsync(string accessToken, DateOnly date);
}
