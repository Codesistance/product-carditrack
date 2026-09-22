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

    /// <summary>
    /// The day's ECG readings and irregular-rhythm notifications, with the analysis windows and
    /// beats behind the latter. Call only for a connection whose granted scopes include the two
    /// rhythm scopes — see <see cref="DeviceRhythmDay"/> for why the gate is the caller's.
    /// </summary>
    Task<DeviceRhythmDay> GetRhythmDayAsync(string accessToken, DateOnly date);

    /// <summary>
    /// The wearer's Irregular Rhythm Notifications onboarding and enrolment status, or nulls where
    /// the read is not permitted.
    /// </summary>
    Task<(bool? Onboarded, bool? Enrolled)> GetIrnProfileAsync(string accessToken);
}
