using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

public class AlertPreferenceService : IAlertPreferenceService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public AlertPreferenceService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<AlertPreferencesResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);
        await RequireActiveMemberAsync(cardiMemberId);
        var overrides = await GetOverridesAsync(cardiMemberId, ct);
        return BuildResponse(cardiMemberId, overrides);
    }

    public async Task<AlertRuleSettingResponse> SetRuleEnabledAsync(
        Guid requestingUserId, Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        await RequireActiveMemberAsync(cardiMemberId);

        if (string.IsNullOrWhiteSpace(ruleId) || !AlertRuleCatalogue.IsKnown(ruleId))
            throw new ArgumentException("Unknown alert rule.", nameof(ruleId));

        if (!AlertRuleCatalogue.IsImplemented(ruleId))
            throw new InvalidOperationException("That alert rule is not available yet.");

        var existing = await _unitOfWork.AlertPreferences.GetByCardiMemberIdAsync(cardiMemberId, ct);
        var current = AlertRuleOverrides.FromJson(existing?.DisabledRules);
        var next = current.WithEnabled(ruleId, enabled);

        if (existing is null)
        {
            // Only persist when something is actually off — an all-on row is noise.
            if (next.DisabledRuleIds.Count > 0)
            {
                await _unitOfWork.AlertPreferences.AddAsync(new AlertPreference
                {
                    CardiMemberId = cardiMemberId,
                    DisabledRules = next.ToJson(),
                });
                await _unitOfWork.SaveChangesAsync();
            }
        }
        else if (next.DisabledRuleIds.Count == 0)
        {
            // Back to defaults — drop the row so "no preference" stays the on-by-default path.
            _unitOfWork.AlertPreferences.Remove(existing);
            await _unitOfWork.SaveChangesAsync();
        }
        else
        {
            existing.DisabledRules = next.ToJson();
            existing.UpdatedDate = DateTime.UtcNow;
            _unitOfWork.AlertPreferences.Update(existing);
            await _unitOfWork.SaveChangesAsync();
        }

        var def = AlertRuleCatalogue.Clusters
            .SelectMany(c => c.Rules)
            .First(r => r.Id == ruleId);

        return new AlertRuleSettingResponse
        {
            Id = def.Id,
            Title = def.Title,
            Description = def.Description,
            Enabled = enabled,
            IsImplemented = def.IsImplemented,
        };
    }

    public async Task<AlertRuleOverrides> GetOverridesAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        var row = await _unitOfWork.AlertPreferences.GetByCardiMemberIdAsync(cardiMemberId, ct);
        return AlertRuleOverrides.FromJson(row?.DisabledRules);
    }

    private async Task RequireActiveMemberAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
    }

    private static AlertPreferencesResponse BuildResponse(Guid cardiMemberId, AlertRuleOverrides overrides) =>
        new()
        {
            CardiMemberId = cardiMemberId,
            Clusters = AlertRuleCatalogue.Clusters.Select(c => new AlertRuleClusterResponse
            {
                Id = c.Id,
                Title = c.Title,
                Description = c.Description,
                Rules = c.Rules.Select(r => new AlertRuleSettingResponse
                {
                    Id = r.Id,
                    Title = r.Title,
                    Description = r.Description,
                    Enabled = overrides.IsEnabled(r.Id),
                    IsImplemented = r.IsImplemented,
                }).ToList(),
            }).ToList(),
        };
}
