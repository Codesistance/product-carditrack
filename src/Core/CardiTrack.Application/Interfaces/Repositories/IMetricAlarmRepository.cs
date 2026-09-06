using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMetricAlarmRepository : IRepository<MetricAlarm>
{
    /// <summary>
    /// Every live alarm row for one organization — both account-level defaults and per-member rows.
    /// One read serves the whole resolution, because <c>MetricAlarmResolution</c> needs the account
    /// rows and the member's own rows together to tell an override from an addition.
    /// </summary>
    Task<IReadOnlyList<MetricAlarm>> GetByOrganizationAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// The alarms applying to one member, without loading the rest of the organization's — the
    /// engine's per-member read. Returns account-level defaults plus that member's own rows.
    /// </summary>
    Task<IReadOnlyList<MetricAlarm>> GetForMemberAsync(
        Guid organizationId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Live alarms by id, scoped to the organization so a Guid from another tenant
    /// resolves to nothing rather than to somebody else's alarm.</summary>
    Task<MetricAlarm?> GetByIdAsync(Guid organizationId, Guid alarmId, CancellationToken ct = default);

    /// <summary>Member rows that override a given account-level alarm — what a delete has to
    /// account for, and what a "revert to default" has to remove.</summary>
    Task<IReadOnlyList<MetricAlarm>> GetOverridesOfAsync(Guid accountAlarmId, CancellationToken ct = default);

    /// <summary>
    /// Organizations with at least one enabled alarm. The engine's outer filter: without it every
    /// tick would walk every member in the estate to discover that almost none of them have an
    /// alarm defined.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsWithEnabledAlarmsAsync(CancellationToken ct = default);
}
