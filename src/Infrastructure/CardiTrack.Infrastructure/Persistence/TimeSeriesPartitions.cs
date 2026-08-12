using System.Globalization;

namespace CardiTrack.Infrastructure.Persistence;

/// <summary>
/// Pure naming and DDL arithmetic for the partitioned time-series tables — everything about
/// partitions that can be unit-tested without a database. `GranularMetricHours` partitions daily
/// (short retention, small drops); `MetricRollupsHourly` partitions monthly (longer retention,
/// far fewer rows).
/// <para>
/// All SQL built here interpolates only compile-time table names and formatted dates — no user
/// input ever reaches these strings, which is what makes raw DDL acceptable.
/// </para>
/// </summary>
public static class TimeSeriesPartitions
{
    public const string GranularParent = "GranularMetricHours";
    public const string RollupParent = "MetricRollupsHourly";
    public const string DigestParent = "DigestEntries";
    public const string RealtimeParent = "RealtimeAssessments";
    public const string EnvironmentalParent = "EnvironmentalReadings";

    private const string DailySuffixFormat = "yyyyMMdd";
    private const string MonthlySuffixFormat = "yyyyMM";

    public static string DailyPartitionName(DateOnly day) =>
        $"{GranularParent}_y{day.ToString(DailySuffixFormat, CultureInfo.InvariantCulture)}";

    public static string MonthlyPartitionName(DateOnly firstOfMonth) =>
        $"{RollupParent}_y{firstOfMonth.ToString(MonthlySuffixFormat, CultureInfo.InvariantCulture)}";

    public static string DigestPartitionName(DateOnly firstOfMonth) =>
        $"{DigestParent}_y{firstOfMonth.ToString(MonthlySuffixFormat, CultureInfo.InvariantCulture)}";

    public static string RealtimePartitionName(DateOnly day) =>
        $"{RealtimeParent}_y{day.ToString(DailySuffixFormat, CultureInfo.InvariantCulture)}";

    public static string EnvironmentalPartitionName(DateOnly day) =>
        $"{EnvironmentalParent}_y{day.ToString(DailySuffixFormat, CultureInfo.InvariantCulture)}";

    public static string CreateDailyPartitionSql(DateOnly day) =>
        $"""
        CREATE TABLE IF NOT EXISTS "{DailyPartitionName(day)}"
        PARTITION OF "{GranularParent}"
        FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}');
        """;

    public static string CreateMonthlyPartitionSql(DateOnly firstOfMonth) =>
        $"""
        CREATE TABLE IF NOT EXISTS "{MonthlyPartitionName(firstOfMonth)}"
        PARTITION OF "{RollupParent}"
        FOR VALUES FROM ('{firstOfMonth:yyyy-MM-dd}') TO ('{firstOfMonth.AddMonths(1):yyyy-MM-dd}');
        """;

    public static string CreateDigestPartitionSql(DateOnly firstOfMonth) =>
        $"""
        CREATE TABLE IF NOT EXISTS "{DigestPartitionName(firstOfMonth)}"
        PARTITION OF "{DigestParent}"
        FOR VALUES FROM ('{firstOfMonth:yyyy-MM-dd}') TO ('{firstOfMonth.AddMonths(1):yyyy-MM-dd}');
        """;

    public static string CreateRealtimePartitionSql(DateOnly day) =>
        $"""
        CREATE TABLE IF NOT EXISTS "{RealtimePartitionName(day)}"
        PARTITION OF "{RealtimeParent}"
        FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}');
        """;

    public static string CreateEnvironmentalPartitionSql(DateOnly day) =>
        $"""
        CREATE TABLE IF NOT EXISTS "{EnvironmentalPartitionName(day)}"
        PARTITION OF "{EnvironmentalParent}"
        FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}');
        """;

    public static string DropPartitionSql(string partitionName) =>
        $"""DROP TABLE IF EXISTS "{partitionName}";""";

    /// <summary>
    /// The day a daily granular partition covers, parsed back out of its name; false for any
    /// child that does not follow the naming scheme (never drop what we did not name).
    /// </summary>
    public static bool TryParseDailyPartition(string partitionName, out DateOnly day) =>
        TryParseSuffix(partitionName, $"{GranularParent}_y", DailySuffixFormat, out day);

    /// <summary>The first day of the month a monthly rollup partition covers.</summary>
    public static bool TryParseMonthlyPartition(string partitionName, out DateOnly firstOfMonth) =>
        TryParseMonthSuffix(partitionName, $"{RollupParent}_y", out firstOfMonth);

    /// <summary>The first day of the month a digest partition covers.</summary>
    public static bool TryParseDigestPartition(string partitionName, out DateOnly firstOfMonth) =>
        TryParseMonthSuffix(partitionName, $"{DigestParent}_y", out firstOfMonth);

    /// <summary>The day a daily real-time assessment partition covers.</summary>
    public static bool TryParseRealtimePartition(string partitionName, out DateOnly day) =>
        TryParseSuffix(partitionName, $"{RealtimeParent}_y", DailySuffixFormat, out day);

    /// <summary>The day a daily environmental-reading partition covers.</summary>
    public static bool TryParseEnvironmentalPartition(string partitionName, out DateOnly day) =>
        TryParseSuffix(partitionName, $"{EnvironmentalParent}_y", DailySuffixFormat, out day);

    private static bool TryParseMonthSuffix(string partitionName, string prefix, out DateOnly firstOfMonth)
    {
        firstOfMonth = default;
        if (!partitionName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = partitionName[prefix.Length..];
        if (!DateTime.TryParseExact(suffix, MonthlySuffixFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return false;

        firstOfMonth = DateOnly.FromDateTime(parsed);
        return true;
    }

    private static bool TryParseSuffix(
        string partitionName, string prefix, string format, out DateOnly day)
    {
        day = default;
        if (!partitionName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return DateOnly.TryParseExact(
            partitionName[prefix.Length..], format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
    }
}
