namespace CardiTrack.Application.Services;

/// <summary>
/// Canonical alert-rule ids and their settings clusters. Ids match the <c>rule</c> stamp in
/// <c>Alert.MetricValues</c> and the producer constants in <see cref="StatisticalAlertRules"/> /
/// <c>AlertRuleMarkers</c>. Future A–G rules ship here with <see cref="AlertRuleDefinition.IsImplemented"/>
/// false until their producer lands — settings can show them as coming soon without inventing ids later.
/// </summary>
public static class AlertRuleCatalogue
{
    // Existing producers
    public const string ActivityDecline = StatisticalAlertRules.ActivityDeclineRule;
    public const string IrregularSleep = StatisticalAlertRules.IrregularSleepRule;
    public const string ElevatedHeartRate = StatisticalAlertRules.ElevatedHeartRateRule;
    public const string NoMorningActivity = StatisticalAlertRules.NoMorningActivityRule;
    public const string LongTermTrend = StatisticalAlertRules.LongTermTrendRule;
    public const string DeviceSilence = "device_silence";
    public const string RealtimeHeartRate = "realtime_hr";
    public const string HeartRateVariabilityDrop = StatisticalAlertRules.HeartRateVariabilityDropRule;
    public const string OvernightBreathingUp = StatisticalAlertRules.OvernightBreathingUpRule;
    public const string ElevatedZoneWithoutMovement = StatisticalAlertRules.ElevatedZoneWithoutMovementRule;

    // A–G (catalogue reserved; producers not shipped yet)
    public const string LateBedtime = "late_bedtime";
    public const string FragmentedSleep = "fragmented_sleep";
    public const string OvernightVitals = "overnight_vitals";
    public const string LowMorningMobility = "low_morning_mobility";
    public const string DaytimeInactivityBlock = StatisticalAlertRules.DaytimeInactivityBlockRule;
    public const string MultiSignalCluster = "multi_signal_cluster";
    public const string BaselineShift = "baseline_shift";

    public const string ClusterSleep = "sleep";
    public const string ClusterHeart = "heart_overnight";
    public const string ClusterActivity = "activity_mobility";
    public const string ClusterPattern = "pattern";
    public const string ClusterDigests = "digests_trends";

    private static readonly IReadOnlyList<AlertRuleClusterDefinition> ClustersInternal =
        Array.AsReadOnly(new AlertRuleClusterDefinition[]
        {
            new(ClusterSleep, "Sleep", "Bedtime, sleep quality, and unusual daytime rest",
            [
                new(LateBedtime, "Late or missed bedtime", "Still active past their usual bedtime", IsImplemented: false),
                new(FragmentedSleep, "Restless night", "More wake-ups or awake time than usual", IsImplemented: false),
                new(IrregularSleep, "Unusual sleep length", "Last night was much shorter than usual, or well past the recommended hours", IsImplemented: true),
                new(DaytimeInactivityBlock, "Long daytime rest", "An unusually long inactive stretch in waking hours", IsImplemented: true),
            ]),
            new(ClusterHeart, "Heart & overnight", "Resting heart rate and overnight vitals",
            [
                new(ElevatedHeartRate, "Elevated resting heart rate", "Yesterday's resting rate was higher than usual", IsImplemented: true),
                new(OvernightVitals, "Unusual overnight vitals", "Heart rate or SpO₂ looked off during sleep", IsImplemented: false),
                new(RealtimeHeartRate, "Sudden heart-rate change", "A sharp change in the last hour of heart-rate data", IsImplemented: true),
                new(HeartRateVariabilityDrop, "Heart rate variability has dropped", "Two nights running well below their usual — often the first sign of illness or strain", IsImplemented: true),
                new(OvernightBreathingUp, "Breathing faster overnight", "Breathing while asleep running above their own usual", IsImplemented: true),
                new(ElevatedZoneWithoutMovement, "Heart working on a quiet day", "Real time in a raised heart-rate zone on a day they barely moved", IsImplemented: true),
            ]),
            new(ClusterActivity, "Activity & mobility", "Steps, morning movement, and a quiet device",
            [
                new(ActivityDecline, "Activity decline", "Yesterday's steps were well below their usual", IsImplemented: true),
                new(LowMorningMobility, "Low morning mobility", "Awake but barely moving for longer than usual", IsImplemented: false),
                new(NoMorningActivity, "No morning activity", "No steps well after their usual wake time", IsImplemented: true),
                new(LongTermTrend, "Declining activity trend", "Several weeks of steadily fewer steps", IsImplemented: true),
                new(DeviceSilence, "Device gone quiet", "No readings from the wearable for several waking hours", IsImplemented: true),
            ]),
            new(ClusterPattern, "Pattern", "Several signals off at once",
            [
                new(MultiSignalCluster, "Several things off together", "More than one unusual reading on the same day", IsImplemented: false),
            ]),
            new(ClusterDigests, "Digests & trends", "Slow shifts, usually in the daily summary",
            [
                new(BaselineShift, "Baseline shift", "Their usual bedtime or activity level has drifted", IsImplemented: false),
            ]),
        });

    public static IReadOnlyList<AlertRuleClusterDefinition> Clusters => ClustersInternal;

    private static readonly HashSet<string> KnownIds =
        ClustersInternal.SelectMany(c => c.Rules).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> ImplementedIds =
        ClustersInternal.SelectMany(c => c.Rules).Where(r => r.IsImplemented).Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);

    public static bool IsKnown(string ruleId) => KnownIds.Contains(ruleId);

    public static bool IsImplemented(string ruleId) => ImplementedIds.Contains(ruleId);
}

public sealed record AlertRuleClusterDefinition(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<AlertRuleDefinition> Rules);

public sealed record AlertRuleDefinition(
    string Id,
    string Title,
    string Description,
    bool IsImplemented);
