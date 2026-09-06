using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMetricAlarmStateRepository : IRepository<MetricAlarmState>
{
    /// <summary>Every stored state for one member, in one read — the engine holds them all for the
    /// tick rather than fetching per alarm.</summary>
    Task<IReadOnlyList<MetricAlarmState>> GetByCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Discards the states belonging to an alarm that has been deleted, so a later alarm
    /// can never inherit a stale standing state through a reused id.</summary>
    Task DeleteForAlarmAsync(Guid metricAlarmId, CancellationToken ct = default);
}
