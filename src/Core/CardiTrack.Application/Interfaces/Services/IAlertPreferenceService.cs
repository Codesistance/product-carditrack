using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;

namespace CardiTrack.Application.Interfaces.Services;

public interface IAlertPreferenceService
{
    Task<AlertPreferencesResponse> GetAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <param name="expectedDisabledRules">
    /// The member's disabled-rule list as the caller last saw it — <see cref="AlertRuleOverrides.ToJson"/>
    /// of the effective overrides — or null to write unconditionally. When given, the write is
    /// refused with <see cref="Exceptions.AlertSettingsChangedException"/> if the list has moved
    /// since: a chat proposal confirmed after someone else flipped a rule must not put their
    /// change back.
    /// </param>
    Task<AlertRuleSettingResponse> SetRuleEnabledAsync(
        Guid requestingUserId, Guid cardiMemberId, string ruleId, bool enabled,
        string? expectedDisabledRules = null, CancellationToken ct = default);

    /// <summary>
    /// Worker / pipeline path — no access check. Missing row means all rules on.
    /// </summary>
    Task<AlertRuleOverrides> GetOverridesAsync(Guid cardiMemberId, CancellationToken ct = default);
}
