using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Reading and writing user-defined alarms, with access enforced. Every method takes the requesting
/// user because scope resolution and the manage-access bar both depend on who is asking.
/// </summary>
public interface IMetricAlarmService
{
    /// <summary>The evaluable combinations, for the builder UI.</summary>
    AlarmCatalogueResponse GetCatalogue();

    /// <summary>The organization's account-level defaults.</summary>
    Task<IReadOnlyList<MetricAlarmResponse>> GetAccountAlarmsAsync(
        Guid requestingUserId, CancellationToken ct = default);

    /// <summary>
    /// The alarms that actually apply to one member — account defaults folded together with the
    /// member's own rows, each carrying where it came from and where it currently stands.
    /// </summary>
    Task<IReadOnlyList<MetricAlarmResponse>> GetMemberAlarmsAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    Task<MetricAlarmResponse> CreateAccountAlarmAsync(
        Guid requestingUserId, SaveMetricAlarmRequest request, CancellationToken ct = default);

    Task<MetricAlarmResponse> UpdateAccountAlarmAsync(
        Guid requestingUserId, Guid alarmId, SaveMetricAlarmRequest request, CancellationToken ct = default);

    Task DeleteAccountAlarmAsync(Guid requestingUserId, Guid alarmId, CancellationToken ct = default);

    /// <summary>Adds an alarm for one member alone.</summary>
    Task<MetricAlarmResponse> CreateMemberAlarmAsync(
        Guid requestingUserId, Guid cardiMemberId, SaveMetricAlarmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Replaces what applies to this member for one alarm. Given an account-level alarm's id this
    /// writes (or updates) the member's override of it; given a member alarm's own id it edits that
    /// row. Saving with <c>IsEnabled</c> false is how a member opts out of an inherited alarm.
    /// </summary>
    Task<MetricAlarmResponse> SaveMemberOverrideAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid alarmId, SaveMetricAlarmRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Removes what this member has of their own for an alarm. On an override that means reverting
    /// to the account default; on a member-only alarm it means deleting it.
    /// </summary>
    Task DeleteMemberAlarmAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid alarmId, CancellationToken ct = default);
}
