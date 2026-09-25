using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Rebuilds the derived ActivityLog row for a CardiMember-day from that member's raw per-device
/// rows, and says what is known about the night that ended on it. See <see cref="ActivityLogMerge"/>
/// for the merge rule and <see cref="NightSleepClassifier"/> for the night's.
/// </summary>
public class ActivityLogAggregationService : IActivityLogAggregationService
{
    private readonly IDeviceActivityLogRepository _deviceActivityLogs;
    private readonly IActivityLogRepository _activityLogs;
    private readonly IDeviceConnectionRepository _deviceConnections;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;

    public ActivityLogAggregationService(
        IDeviceActivityLogRepository deviceActivityLogs,
        IActivityLogRepository activityLogs,
        IDeviceConnectionRepository deviceConnections,
        IUnitOfWork unitOfWork,
        TimeProvider? clock = null)
    {
        _deviceActivityLogs = deviceActivityLogs;
        _activityLogs = activityLogs;
        _deviceConnections = deviceConnections;
        _unitOfWork = unitOfWork;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task RecomputeAsync(Guid cardiMemberId, DateOnly date)
    {
        var rawRows = (await _deviceActivityLogs.GetByCardiMemberAndDateAsync(cardiMemberId, date)).ToList();
        if (rawRows.Count == 0)
            return;

        // Order the raw rows by their connection's priority. Connections are read fresh rather
        // than cached so a change of primary device takes effect on the next sync.
        var connections = await _deviceConnections.GetByCardiMemberIdAsync(cardiMemberId);
        var priority = ActivityLogMerge.ByPriority(connections)
            .Select((c, index) => (c.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        // A row whose connection has since been removed still holds real history, so it is kept
        // and simply sorted last rather than dropped from the merge.
        var rowsByPriority = rawRows
            .OrderBy(r => priority.TryGetValue(r.DeviceConnectionId, out var rank) ? rank : int.MaxValue)
            .ThenBy(r => r.DeviceConnectionId)
            .ToList();

        var merged = ActivityLogMerge.Merge(cardiMemberId, date, rowsByPriority);
        if (merged is null)
            return;

        CarryNightForward(merged, await StoredRowAsync(cardiMemberId, date));
        await _activityLogs.UpsertAsync(merged);
    }

    public async Task ClassifyNightAsync(Guid cardiMemberId, DateOnly date, CancellationToken ct = default)
    {
        var row = await StoredRowAsync(cardiMemberId, date);
        if (row is null)
            return;

        // The provider's own figure, as the merge wrote it — an awake night's 0 is ours, not theirs.
        var providerSleep = row.NightStatus == NightSleepStatus.Awake ? null : row.SleepMinutes;

        var status = providerSleep is not null
            ? NightSleepStatus.Slept
            : await ClassifyWithoutSessionAsync(cardiMemberId, date, ct);

        var sleepMinutes = status == NightSleepStatus.Awake ? 0 : providerSleep;
        if (row.NightStatus == status && row.SleepMinutes == sleepMinutes)
            return;

        row.NightStatus = status;
        row.SleepMinutes = sleepMinutes;
        await _activityLogs.UpsertAsync(row);
    }

    /// <summary>
    /// Keeps what an earlier classification established across a re-merge. The merge rebuilds
    /// the row from the raw device rows, which know nothing of the night's status; without this,
    /// every sync would wipe an awake night back to a null for the seconds until the heart rate
    /// is read again — and a caregiver reading in those seconds would see a sleepless night as a
    /// missing one.
    /// </summary>
    /// <remarks>
    /// A session in the merge is decisive: the night was slept, whatever was said before. With no
    /// session, the stored status stands — including an awake night's 0 — except a stored
    /// <see cref="NightSleepStatus.Slept"/>, which a provider that has since withdrawn the session
    /// no longer supports; that night goes back to unclassified until
    /// <see cref="ClassifyNightAsync"/> reads it again.
    /// </remarks>
    private static void CarryNightForward(ActivityLog merged, ActivityLog? stored)
    {
        if (merged.SleepMinutes is not null)
        {
            merged.NightStatus = NightSleepStatus.Slept;
            return;
        }

        if (stored?.NightStatus is not { } status || status == NightSleepStatus.Slept)
            return;

        merged.NightStatus = status;
        if (status == NightSleepStatus.Awake)
            merged.SleepMinutes = 0;
    }

    private async Task<NightSleepStatus> ClassifyWithoutSessionAsync(
        Guid cardiMemberId, DateOnly date, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, periodDays: 30);
        var zone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, cardiMemberId);
        var window = NightSleepClassifier.NightWindowUtc(date, baseline, zone);

        if (now < window.ToUtc)
            return NightSleepClassifier.Classify(null, 0, 0, syncedSinceWake: false, windowOver: false);

        // Whole hours, per the granular read contract, from the hour bedtime falls in to the hour
        // after the latest moment that can show a post-wake sync: now, or half a day past waking
        // for a night being re-read days later — enough to see the morning's sync without reading
        // the whole of every day after it.
        var fromUtc = FloorHour(window.FromUtc);
        var toUtc = CeilingHour(Min(now, window.ToUtc.AddHours(12)));
        var granular = await _unitOfWork.GranularMetrics.GetWindowAsync(cardiMemberId, fromUtc, toUtc, ct);

        var heartRate = granular.MinuteSeries.TryGetValue(GranularMetric.HeartRate, out var series)
            ? series
            : [];
        var (worn, synced) = NightSleepClassifier.Read(heartRate, fromUtc, window);
        var windowMinutes = (int)(window.ToUtc - window.FromUtc).TotalMinutes;

        return NightSleepClassifier.Classify(null, worn, windowMinutes, synced, windowOver: true);
    }

    private async Task<ActivityLog?> StoredRowAsync(Guid cardiMemberId, DateOnly date) =>
        (await _activityLogs.GetByCardiMemberAndDateRangeAsync(cardiMemberId, date, date)).FirstOrDefault();

    private static DateTime FloorHour(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private static DateTime CeilingHour(DateTime utc)
    {
        var floor = FloorHour(utc);
        return floor == utc ? floor : floor.AddHours(1);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
