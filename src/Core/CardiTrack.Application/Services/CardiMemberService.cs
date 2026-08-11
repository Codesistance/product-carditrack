using System.Security.Cryptography;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class CardiMemberService : ICardiMemberService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly IEncryptionService _encryption;
    private readonly INotificationGapResolver _gapResolver;

    public CardiMemberService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IEncryptionService encryption,
        INotificationGapResolver gapResolver)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _encryption = encryption;
        _gapResolver = gapResolver;
    }

    public async Task<CardiMemberResponse> CreateCardiMemberAsync(
        Guid organizationId,
        Guid userId,
        CreateCardiMemberRequest request)
    {
        var cardiMember = new CardiMember
        {
            OrganizationId = organizationId,
            Name = request.Name,
            DateOfBirth = request.DateOfBirth,
            Gender = request.Gender,
            Email = request.Email,
            Phone = request.Phone,
            EmergencyContactName = request.EmergencyContactName,
            EmergencyContactPhone = request.EmergencyContactPhone,
            MedicalNotes = Protect(request.MedicalNotes),
            IsActive = true
        };

        await _unitOfWork.CardiMembers.AddAsync(cardiMember);
        await _unitOfWork.SaveChangesAsync(); // Save to get ID

        // Create relationship between user and CardiMember
        var userCardiMember = new UserCardiMember
        {
            UserId = userId,
            CardiMemberId = cardiMember.Id,
            RelationshipType = request.RelationshipType,
            IsPrimaryCaregiver = request.IsPrimaryCaregiver,
            CanViewHealthData = true,
            ReceiveAlerts = true
        };

        await _unitOfWork.UserCardiMembers.AddAsync(userCardiMember);
        await _unitOfWork.SaveChangesAsync();

        return new CardiMemberResponse
        {
            Id = cardiMember.Id,
            Name = cardiMember.Name,
            DateOfBirth = cardiMember.DateOfBirth,
            Age = CalculateAge(cardiMember.DateOfBirth),
            Gender = cardiMember.Gender,
            Email = cardiMember.Email,
            Phone = cardiMember.Phone,
            Relationship = request.RelationshipType,
            IsPrimaryCaregiver = request.IsPrimaryCaregiver,
            IsActive = cardiMember.IsActive,
            CreatedDate = cardiMember.CreatedDate
        };
    }

    public async Task<CardiMemberResponse?> GetByIdAsync(Guid id)
    {
        var cardiMember = await _unitOfWork.CardiMembers.GetByIdAsync(id);
        if (cardiMember == null) return null;

        // Get relationship info - assuming first relationship for now
        var relationships = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(id);
        var primaryRelationship = relationships.FirstOrDefault();

        return new CardiMemberResponse
        {
            Id = cardiMember.Id,
            Name = cardiMember.Name,
            DateOfBirth = cardiMember.DateOfBirth,
            Age = CalculateAge(cardiMember.DateOfBirth),
            Gender = cardiMember.Gender,
            Email = cardiMember.Email,
            Phone = cardiMember.Phone,
            Relationship = primaryRelationship?.RelationshipType ?? Domain.Enums.RelationshipType.Other,
            IsPrimaryCaregiver = primaryRelationship?.IsPrimaryCaregiver ?? false,
            IsActive = cardiMember.IsActive,
            CreatedDate = cardiMember.CreatedDate
        };
    }

    public async Task<List<CardiMemberResponse>> GetByOrganizationIdAsync(Guid organizationId)
    {
        var cardiMembers = await _unitOfWork.CardiMembers.GetByOrganizationIdAsync(organizationId);
        var responses = new List<CardiMemberResponse>();

        foreach (var cm in cardiMembers)
        {
            var relationships = await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cm.Id);
            var primaryRelationship = relationships.FirstOrDefault();

            responses.Add(new CardiMemberResponse
            {
                Id = cm.Id,
                Name = cm.Name,
                DateOfBirth = cm.DateOfBirth,
                Age = CalculateAge(cm.DateOfBirth),
                Gender = cm.Gender,
                Email = cm.Email,
                Phone = cm.Phone,
                Relationship = primaryRelationship?.RelationshipType ?? Domain.Enums.RelationshipType.Other,
                IsPrimaryCaregiver = primaryRelationship?.IsPrimaryCaregiver ?? false,
                IsActive = cm.IsActive,
                CreatedDate = cm.CreatedDate
            });
        }

        return responses;
    }

    public async Task<CardiMemberDetailResponse> GetDetailAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);
        return await BuildDetailAsync(requestingUserId, member);
    }

    public async Task<CardiMemberDetailResponse> UpdateAsync(
        Guid requestingUserId, Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        member.Name = request.Name;
        member.DateOfBirth = request.DateOfBirth;
        member.Email = request.Email;
        member.Phone = request.Phone;
        member.EmergencyContactName = request.EmergencyContactName;
        member.EmergencyContactPhone = request.EmergencyContactPhone;
        member.MedicalNotes = Protect(request.MedicalNotes);
        member.AlertSensitivity = request.AlertSensitivity;
        member.UpdatedDate = DateTime.UtcNow;
        _unitOfWork.CardiMembers.Update(member);

        // Relationship lives on the caregiver's own link, not the member — editing it here
        // must not rewrite what other caregivers call this person.
        var link = await FindLinkAsync(requestingUserId, cardiMemberId);
        if (link is not null && link.RelationshipType != request.RelationshipType)
        {
            link.RelationshipType = request.RelationshipType;
            link.UpdatedDate = DateTime.UtcNow;
            _unitOfWork.UserCardiMembers.Update(link);
        }

        await _unitOfWork.SaveChangesAsync();

        // Saving may have closed a gap we are currently nagging about. Resolving here rather than
        // waiting for the nightly run is what stops a caregiver seeing the card they just actioned
        // still sitting there when the screen pops.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return await BuildDetailAsync(requestingUserId, member);
    }

    public async Task RemoveAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.IsActive = false;
        member.UpdatedDate = now;
        _unitOfWork.CardiMembers.Update(member);

        // Deactivate the links too, otherwise the member keeps passing access checks and
        // keeps being counted by anything that walks a caregiver's links.
        foreach (var link in await _unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId))
        {
            if (!link.IsActive) continue;
            link.IsActive = false;
            link.UpdatedDate = now;
            _unitOfWork.UserCardiMembers.Update(link);
        }

        // Devices must stop syncing, and their tokens should not outlive the member.
        foreach (var connection in await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId))
        {
            if (!connection.IsActive) continue;
            connection.IsActive = false;
            connection.ConnectionStatus = ConnectionStatus.Disconnected;
            connection.AccessToken = null;
            connection.RefreshToken = null;
            connection.TokenExpiry = null;
            connection.UpdatedDate = now;
            _unitOfWork.DeviceConnections.Update(connection);
        }

        await _unitOfWork.SaveChangesAsync();

        // Withdrawn rather than resolved: the gaps were not closed, they stopped applying. Counting
        // a removal as a success would flatter the comply rate the rule review depends on.
        await _gapResolver.WithdrawForCardiMemberAsync(
            cardiMemberId, NotificationResolutionReason.ScopeRemoved, ct);
    }

    public async Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.MonitoringPausedUntil = now.AddHours(request.DurationHours);
        member.MonitoringPauseReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        member.UpdatedDate = now;
        _unitOfWork.CardiMembers.Update(member);
        await _unitOfWork.SaveChangesAsync();

        // Nagging about a member someone deliberately paused is the fastest way to teach a
        // caregiver to ignore the whole surface.
        await _gapResolver.WithdrawForCardiMemberAsync(
            cardiMemberId, NotificationResolutionReason.MonitoringPaused, ct);

        return PauseStateOf(member, now);
    }

    public async Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = DateTime.UtcNow;
        member.MonitoringPausedUntil = null;
        member.MonitoringPauseReason = null;
        member.UpdatedDate = now;
        _unitOfWork.CardiMembers.Update(member);
        await _unitOfWork.SaveChangesAsync();

        // Anything still outstanding comes straight back rather than waiting for tomorrow's run.
        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return PauseStateOf(member, now);
    }

    private async Task<CardiMember> RequireActiveMemberAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
        return member;
    }

    private async Task<UserCardiMember?> FindLinkAsync(Guid userId, Guid cardiMemberId)
    {
        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(userId);
        return links.FirstOrDefault(l => l.CardiMemberId == cardiMemberId && l.IsActive);
    }

    private async Task<CardiMemberDetailResponse> BuildDetailAsync(Guid requestingUserId, CardiMember member)
    {
        // The relationship shown is the requesting caregiver's own ("Dad"), not whatever the
        // first link in the table happens to say.
        var link = await FindLinkAsync(requestingUserId, member.Id);
        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(member.Id)).ToList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logs = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                member.Id, today.AddDays(-(BaselineProgress.PeriodDays - 1)), today))
            .ToList();
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(
            member.Id, BaselineProgress.PeriodDays);
        var unresolvedAlerts = (await _unitOfWork.Alerts.GetByCardiMemberAsync(member.Id, activeOnly: true))
            .Where(a => !a.IsResolved)
            .ToList();

        var now = DateTime.UtcNow;
        var pause = PauseStateOf(member, now);
        var metrics = logs.Count == 0 ? null : MemberInsightsCalculator.BuildMetrics(logs, baseline, today);

        return new CardiMemberDetailResponse
        {
            Id = member.Id,
            Name = member.Name,
            DateOfBirth = member.DateOfBirth,
            Age = CalculateAge(member.DateOfBirth),
            Gender = member.Gender,
            Email = member.Email,
            Phone = member.Phone,
            Relationship = link?.RelationshipType ?? RelationshipType.Other,
            IsPrimaryCaregiver = link?.IsPrimaryCaregiver ?? false,
            EmergencyContactName = member.EmergencyContactName,
            EmergencyContactPhone = member.EmergencyContactPhone,
            MedicalNotes = Reveal(member.MedicalNotes),
            PhotoUrl = null,
            AlertSensitivity = member.AlertSensitivity,
            MonitoringPaused = pause.MonitoringPaused,
            MonitoringPausedUntil = pause.MonitoringPausedUntil,
            MonitoringPauseReason = pause.MonitoringPauseReason,
            MonitoringSince = member.CreatedDate,
            LastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate),
            ConnectedDeviceCount = connections.Count,
            Baseline = BaselineProgress.From(logs, baseline),
            HealthStatus = MemberInsightsCalculator.ComputeHealthStatus(unresolvedAlerts, baseline is null, metrics),
            Metrics = metrics,
        };
    }

    /// <summary>An elapsed pause is reported as not paused, and never reports a stale reason.</summary>
    private static MonitoringPauseResponse PauseStateOf(CardiMember member, DateTime utcNow)
    {
        var paused = member.IsMonitoringPaused(utcNow);
        return new MonitoringPauseResponse
        {
            MonitoringPaused = paused,
            MonitoringPausedUntil = paused ? member.MonitoringPausedUntil : null,
            MonitoringPauseReason = paused ? member.MonitoringPauseReason : null,
        };
    }

    private string? Protect(string? medicalNotes) =>
        string.IsNullOrWhiteSpace(medicalNotes) ? null : _encryption.Encrypt(medicalNotes);

    /// <summary>
    /// Medical notes written before they were encrypted are still sitting in the database as
    /// plain text, and AES-GCM's authentication tag makes those indistinguishable from
    /// corruption. Rather than fail the whole screen, fall back to returning the stored value:
    /// a legacy row reads back as what was typed, and every write re-stores it encrypted.
    /// </summary>
    private string? Reveal(string? storedNotes)
    {
        if (string.IsNullOrEmpty(storedNotes))
            return null;

        try
        {
            return _encryption.Decrypt(storedNotes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return storedNotes;
        }
    }

    private int CalculateAge(DateOnly dateOfBirth)
    {
        var today = DateTime.UtcNow;
        var age = today.Year - dateOfBirth.Year;
        if (today.Month < dateOfBirth.Month || (today.Month == dateOfBirth.Month && today.Day < dateOfBirth.Day))
        {
            age--;
        }
        return age;
    }
}
