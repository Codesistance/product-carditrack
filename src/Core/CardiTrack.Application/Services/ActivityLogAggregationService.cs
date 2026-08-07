using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Rebuilds the derived ActivityLog row for a CardiMember-day from that member's raw per-device
/// rows. See <see cref="ActivityLogMerge"/> for the merge rule itself.
/// </summary>
public class ActivityLogAggregationService : IActivityLogAggregationService
{
    private readonly IDeviceActivityLogRepository _deviceActivityLogs;
    private readonly IActivityLogRepository _activityLogs;
    private readonly IDeviceConnectionRepository _deviceConnections;

    public ActivityLogAggregationService(
        IDeviceActivityLogRepository deviceActivityLogs,
        IActivityLogRepository activityLogs,
        IDeviceConnectionRepository deviceConnections)
    {
        _deviceActivityLogs = deviceActivityLogs;
        _activityLogs = activityLogs;
        _deviceConnections = deviceConnections;
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
        if (merged is not null)
            await _activityLogs.UpsertAsync(merged);
    }
}
