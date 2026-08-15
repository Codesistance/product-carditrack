using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;

namespace CardiTrack.Application.Interfaces.Services;

public interface IAlertPreferenceService
{
    Task<AlertPreferencesResponse> GetAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    Task<AlertRuleSettingResponse> SetRuleEnabledAsync(
        Guid requestingUserId, Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Worker / pipeline path — no access check. Missing row means all rules on.
    /// </summary>
    Task<AlertRuleOverrides> GetOverridesAsync(Guid cardiMemberId, CancellationToken ct = default);
}
