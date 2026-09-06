using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Reading and writing user-defined alarms.
/// <para>
/// <b>Access.</b> Reading a member's alarms needs view access to that member. Writing anything —
/// account-level or per-member — needs primary-caregiver authority: for a member row, over that
/// member; for an account-level default, over at least one member in the organization, since an
/// account default reaches every one of them. Denial throws <see cref="KeyNotFoundException"/> and
/// surfaces as a 404, the same non-disclosure convention the rest of this API uses — a caller must
/// not be able to tell "you may not" from "there is no such thing".
/// </para>
/// </summary>
public class MetricAlarmService : IMetricAlarmService
{
    private const string DeniedMessage = "Alarm not found";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public MetricAlarmService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public AlarmCatalogueResponse GetCatalogue() => new()
    {
        MaxEvaluationPeriods = AlarmMetricCatalogue.MaxEvaluationPeriods,
        Metrics = AlarmMetricCatalogue.Definitions.Select(d => new AlarmMetricOptionResponse
        {
            Metric = d.Metric,
            Title = d.Title,
            Unit = d.Unit,
            Source = d.Source == AlarmMetricSource.Granular ? "granular" : "daily",
            Statistics = d.Statistics,
            PeriodMinutes = d.PeriodMinutes,
            MinThreshold = d.MinThreshold,
            MaxThreshold = d.MaxThreshold,
            SupportsBaselinePercent = d.SupportsBaselinePercent,
            SupportsBaselineSigma = d.SupportsBaselineSigma,
            SupportsContextGate = d.Source == AlarmMetricSource.Granular,
        }).ToList(),
    };

    public async Task<IReadOnlyList<MetricAlarmResponse>> GetAccountAlarmsAsync(
        Guid requestingUserId, CancellationToken ct = default)
    {
        var organizationId = await RequireOrganizationAsync(requestingUserId);
        var rows = await _unitOfWork.MetricAlarms.GetByOrganizationAsync(organizationId, ct);

        return rows
            .Where(a => a.CardiMemberId is null)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => Map(a, provenance: null, state: null))
            .ToList();
    }

    public async Task<IReadOnlyList<MetricAlarmResponse>> GetMemberAlarmsAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireMemberAsync(cardiMemberId);

        var rows = await _unitOfWork.MetricAlarms.GetForMemberAsync(member.OrganizationId, cardiMemberId, ct);
        var resolved = MetricAlarmResolution.Resolve(rows, cardiMemberId);
        var states = (await _unitOfWork.MetricAlarmStates.GetByCardiMemberAsync(cardiMemberId, ct))
            .ToDictionary(s => s.MetricAlarmId);

        return resolved
            .Select(e => Map(e.Alarm, e.Provenance, states.GetValueOrDefault(e.Alarm.Id)))
            .ToList();
    }

    public async Task<MetricAlarmResponse> CreateAccountAlarmAsync(
        Guid requestingUserId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var organizationId = await RequireOrganizationAsync(requestingUserId);
        await RequireAccountWriteAccessAsync(requestingUserId, organizationId);
        Validate(request);

        var alarm = new MetricAlarm { OrganizationId = organizationId };
        Apply(alarm, request);

        await _unitOfWork.MetricAlarms.AddAsync(alarm);
        await _unitOfWork.SaveChangesAsync();

        return Map(alarm, provenance: null, state: null);
    }

    public async Task<MetricAlarmResponse> UpdateAccountAlarmAsync(
        Guid requestingUserId, Guid alarmId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var organizationId = await RequireOrganizationAsync(requestingUserId);
        await RequireAccountWriteAccessAsync(requestingUserId, organizationId);
        Validate(request);

        var alarm = await _unitOfWork.MetricAlarms.GetByIdAsync(organizationId, alarmId, ct);
        if (alarm is null || alarm.CardiMemberId is not null)
            throw new KeyNotFoundException(DeniedMessage);

        Apply(alarm, request);
        _unitOfWork.MetricAlarms.Update(alarm);

        // The alarm's definition has changed, so what it means to be "already in alarm" has too.
        // Clearing the states makes every member re-establish theirs on the next tick, which is
        // what stops a retuned alarm from either re-firing on a condition it was already standing
        // on or staying silent about one it now considers a breach.
        await _unitOfWork.MetricAlarmStates.DeleteForAlarmAsync(alarm.Id, ct);
        await _unitOfWork.SaveChangesAsync();

        return Map(alarm, provenance: null, state: null);
    }

    public async Task DeleteAccountAlarmAsync(Guid requestingUserId, Guid alarmId, CancellationToken ct = default)
    {
        var organizationId = await RequireOrganizationAsync(requestingUserId);
        await RequireAccountWriteAccessAsync(requestingUserId, organizationId);

        var alarm = await _unitOfWork.MetricAlarms.GetByIdAsync(organizationId, alarmId, ct);
        if (alarm is null || alarm.CardiMemberId is not null)
            throw new KeyNotFoundException(DeniedMessage);

        alarm.IsActive = false;
        _unitOfWork.MetricAlarms.Update(alarm);
        await _unitOfWork.MetricAlarmStates.DeleteForAlarmAsync(alarm.Id, ct);

        // Overrides of a deleted default become the members' own alarms — see MetricAlarmResolution.
        // Deliberately not cascaded: a caregiver who tuned this alarm for one person has expressed
        // an intention about that person, and removing the account default is not a retraction of it.
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<MetricAlarmResponse> CreateMemberAlarmAsync(
        Guid requestingUserId, Guid cardiMemberId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireMemberAsync(cardiMemberId);
        Validate(request);
        // A brand-new member alarm replaces nothing.
        await RequireCapacityAsync(member, cardiMemberId, request, replacesEffectiveAlarmId: null, ct);

        var alarm = new MetricAlarm
        {
            OrganizationId = member.OrganizationId,
            CardiMemberId = cardiMemberId,
        };
        Apply(alarm, request);

        await _unitOfWork.MetricAlarms.AddAsync(alarm);
        await _unitOfWork.SaveChangesAsync();

        return Map(alarm, AlarmProvenance.MemberOnly, state: null);
    }

    public async Task<MetricAlarmResponse> SaveMemberOverrideAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid alarmId, SaveMetricAlarmRequest request,
        CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireMemberAsync(cardiMemberId);
        Validate(request);

        var rows = await _unitOfWork.MetricAlarms.GetForMemberAsync(member.OrganizationId, cardiMemberId, ct);
        var target = rows.FirstOrDefault(a => a.Id == alarmId)
            ?? throw new KeyNotFoundException(DeniedMessage);

        MetricAlarm row;
        AlarmProvenance provenance;

        if (target.CardiMemberId == cardiMemberId)
        {
            // Editing the member's own row, whether it is an override or an alarm of their own.
            row = target;
            provenance = target.DerivedFromAlarmId is null ? AlarmProvenance.MemberOnly : AlarmProvenance.Overridden;
            await RequireCapacityAsync(member, cardiMemberId, request, replacesEffectiveAlarmId: row.Id, ct);
            Apply(row, request);
            _unitOfWork.MetricAlarms.Update(row);
        }
        else
        {
            // Overriding an account default for the first time. The new row carries the member's
            // settings and names the default it replaces, so reverting is a delete rather than a
            // re-entry of everything the account said.
            var existing = rows.FirstOrDefault(
                a => a.CardiMemberId == cardiMemberId && a.DerivedFromAlarmId == alarmId);

            provenance = AlarmProvenance.Overridden;
            if (existing is not null)
            {
                // The member's existing override is the row standing in the effective set.
                row = existing;
                await RequireCapacityAsync(member, cardiMemberId, request, replacesEffectiveAlarmId: row.Id, ct);
                Apply(row, request);
                _unitOfWork.MetricAlarms.Update(row);
            }
            else
            {
                // First override of an account default: the default is what this replaces, so the
                // member's effective count does not grow and the ceiling must not be applied as if
                // it did.
                await RequireCapacityAsync(member, cardiMemberId, request, replacesEffectiveAlarmId: alarmId, ct);
                row = new MetricAlarm
                {
                    OrganizationId = member.OrganizationId,
                    CardiMemberId = cardiMemberId,
                    DerivedFromAlarmId = alarmId,
                };
                Apply(row, request);
                await _unitOfWork.MetricAlarms.AddAsync(row);
            }
        }

        await _unitOfWork.MetricAlarmStates.DeleteForAlarmAsync(row.Id, ct);
        if (row.DerivedFromAlarmId is { } replaced)
        {
            // The account default no longer applies here, so the state it left behind for this
            // member must not outlive it and be read as this override's own standing state.
            await ClearStateAsync(replaced, cardiMemberId, ct);
        }

        await _unitOfWork.SaveChangesAsync();
        return Map(row, provenance, state: null);
    }

    public async Task DeleteMemberAlarmAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid alarmId, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireMemberAsync(cardiMemberId);

        var rows = await _unitOfWork.MetricAlarms.GetForMemberAsync(member.OrganizationId, cardiMemberId, ct);

        // Accept either the member row's own id or the account default's — the client's list shows
        // one row per alarm and should not have to know which of the two identities it is holding.
        var row = rows.FirstOrDefault(a => a.CardiMemberId == cardiMemberId && a.Id == alarmId)
            ?? rows.FirstOrDefault(a => a.CardiMemberId == cardiMemberId && a.DerivedFromAlarmId == alarmId)
            ?? throw new KeyNotFoundException(DeniedMessage);

        row.IsActive = false;
        _unitOfWork.MetricAlarms.Update(row);
        await _unitOfWork.MetricAlarmStates.DeleteForAlarmAsync(row.Id, ct);
        await _unitOfWork.SaveChangesAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static void Validate(SaveMetricAlarmRequest request)
    {
        var errors = MetricAlarmValidation.Validate(request);
        if (errors.Count > 0)
            throw new ArgumentException(errors[0].Message, errors[0].Field);
    }

    private static void Apply(MetricAlarm alarm, SaveMetricAlarmRequest request)
    {
        alarm.Name = request.Name.Trim();
        alarm.Metric = request.Metric;
        alarm.Statistic = request.Statistic;
        alarm.Operator = request.Operator;
        alarm.ThresholdKind = request.ThresholdKind;
        alarm.ThresholdValue = request.ThresholdValue;
        alarm.PeriodMinutes = request.PeriodMinutes;
        alarm.EvaluationPeriods = request.EvaluationPeriods;
        alarm.DatapointsToAlarm = request.DatapointsToAlarm;
        alarm.MissingDataTreatment = request.MissingDataTreatment;
        alarm.Severity = request.Severity;
        alarm.ContextGate = request.ContextGate;
        alarm.IsEnabled = request.IsEnabled;
    }

    private static MetricAlarmResponse Map(MetricAlarm alarm, AlarmProvenance? provenance, MetricAlarmState? state) => new()
    {
        Id = alarm.Id,
        CardiMemberId = alarm.CardiMemberId,
        DerivedFromAlarmId = alarm.DerivedFromAlarmId,
        Name = alarm.Name,
        Metric = alarm.Metric,
        Statistic = alarm.Statistic,
        Operator = alarm.Operator,
        ThresholdKind = alarm.ThresholdKind,
        ThresholdValue = alarm.ThresholdValue,
        PeriodMinutes = alarm.PeriodMinutes,
        EvaluationPeriods = alarm.EvaluationPeriods,
        DatapointsToAlarm = alarm.DatapointsToAlarm,
        MissingDataTreatment = alarm.MissingDataTreatment,
        Severity = alarm.Severity,
        ContextGate = alarm.ContextGate,
        IsEnabled = alarm.IsEnabled,
        Condition = MetricAlarmNarrative.Condition(alarm),
        Provenance = provenance?.ToString(),
        State = state?.State,
        StateSinceUtc = state?.StateSinceUtc,
    };

    private async Task<Guid> RequireOrganizationAsync(Guid requestingUserId)
    {
        if (requestingUserId == Guid.Empty)
            throw new KeyNotFoundException(DeniedMessage);

        var user = await _unitOfWork.Users.GetByIdAsync(requestingUserId);
        if (user is null || !user.IsActive)
            throw new KeyNotFoundException(DeniedMessage);

        return user.OrganizationId;
    }

    private async Task<CardiMember> RequireMemberAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException(DeniedMessage);
        return member;
    }

    /// <summary>
    /// An account-level alarm applies to everyone in the organization, so writing one takes the
    /// same authority as writing for a member — held over at least one of them.
    /// </summary>
    private async Task RequireAccountWriteAccessAsync(Guid requestingUserId, Guid organizationId)
    {
        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(requestingUserId);
        var primaryFor = links
            .Where(l => l.IsActive && l.CanViewHealthData && l.IsPrimaryCaregiver)
            .Select(l => l.CardiMemberId)
            .ToList();

        foreach (var memberId in primaryFor)
        {
            var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
            if (member is { IsActive: true } && member.OrganizationId == organizationId)
                return;
        }

        throw new KeyNotFoundException(DeniedMessage);
    }

    /// <summary>
    /// Refuses a save that would push this member past the ceiling on enabled alarms. Not
    /// arbitrary: past a handful, a caregiver stops being able to say what they have switched on,
    /// and an alarm nobody can account for is one they will eventually silence wholesale.
    /// </summary>
    /// <param name="replacesEffectiveAlarmId">
    /// The id of the row this save replaces <b>in the member's effective set</b>, or null when it
    /// genuinely adds one. The distinction is easy to get backwards: writing a member's first
    /// override of an account default creates a row, but it does not add an alarm — the override
    /// takes the default's place. Counting it as an addition refuses the override once a member is
    /// at the ceiling, which is a caregiver being told they cannot tune an alarm they already have.
    /// </param>
    private async Task RequireCapacityAsync(
        CardiMember member, Guid cardiMemberId, SaveMetricAlarmRequest request,
        Guid? replacesEffectiveAlarmId, CancellationToken ct)
    {
        // Turning one off, or saving one that is off, can never push a member past a ceiling.
        if (!request.IsEnabled)
            return;

        var rows = await _unitOfWork.MetricAlarms.GetForMemberAsync(member.OrganizationId, cardiMemberId, ct);
        var othersEnabled = MetricAlarmResolution
            .Evaluable(MetricAlarmResolution.Resolve(rows, cardiMemberId))
            .Count(e => e.Alarm.Id != replacesEffectiveAlarmId);

        if (othersEnabled + 1 > MetricAlarmValidation.MaxEnabledAlarmsPerMember)
        {
            throw new InvalidOperationException(
                $"{member.Name} already has {MetricAlarmValidation.MaxEnabledAlarmsPerMember} alarms switched on. "
                + "Turn one off before adding another.");
        }
    }

    private async Task ClearStateAsync(Guid alarmId, Guid cardiMemberId, CancellationToken ct)
    {
        var states = await _unitOfWork.MetricAlarmStates.GetByCardiMemberAsync(cardiMemberId, ct);
        foreach (var state in states.Where(s => s.MetricAlarmId == alarmId))
            _unitOfWork.MetricAlarmStates.Remove(state);
    }
}
