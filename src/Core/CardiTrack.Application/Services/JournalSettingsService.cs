using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <inheritdoc />
public class JournalSettingsService : IJournalSettingsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public JournalSettingsService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<JournalSettingsResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId)
            ?? throw new KeyNotFoundException("CardiMember not found");

        return await MapAsync(member);
    }

    public async Task<JournalSettingsResponse> UpdateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        UpdateJournalSettingsRequest request,
        CancellationToken ct = default)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);

        Validate(request.DaybookLocalTime, nameof(request.DaybookLocalTime));
        Validate(request.WeekbookLocalTime, nameof(request.WeekbookLocalTime));
        Validate(request.MonthbookLocalTime, nameof(request.MonthbookLocalTime));

        if (request.WeekStartsOn is { } day && !Enum.IsDefined(day))
            throw new ArgumentException("Choose a day of the week.", nameof(request.WeekStartsOn));

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId)
            ?? throw new KeyNotFoundException("CardiMember not found");

        // A full replacement of the four, which is what the dedicated request buys: null here is
        // unambiguously "back to the default" rather than "the client did not send it".
        member.DaybookLocalTime = request.DaybookLocalTime;
        member.WeekbookLocalTime = request.WeekbookLocalTime;
        member.MonthbookLocalTime = request.MonthbookLocalTime;
        member.JournalWeekStartsOn = request.WeekStartsOn;

        _unitOfWork.CardiMembers.Update(member);
        await _unitOfWork.SaveChangesAsync();

        return await MapAsync(member);
    }

    /// <remarks>
    /// Rejects rather than rounds. Silently moving 02:17 to 02:30 would save a time the caregiver
    /// did not choose and then show it back to them as though they had.
    /// </remarks>
    private static void Validate(TimeOnly? time, string field)
    {
        if (JournalSchedule.IsSelectableTime(time))
            return;

        throw new ArgumentException(
            $"Choose a time between {JournalSchedule.EarliestLocalTime:HH:mm} and "
            + $"{JournalSchedule.LatestLocalTime:HH:mm}, on the hour or the half hour.",
            field);
    }

    private async Task<JournalSettingsResponse> MapAsync(CardiMember member)
    {
        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, member.Id);

        return new JournalSettingsResponse
        {
            CardiMemberId = member.Id,

            DaybookLocalTime = member.DaybookLocalTime,
            WeekbookLocalTime = member.WeekbookLocalTime,
            MonthbookLocalTime = member.MonthbookLocalTime,
            WeekStartsOn = member.JournalWeekStartsOn,

            EffectiveDaybookLocalTime = JournalSchedule.EffectiveTime(member.DaybookLocalTime),
            EffectiveWeekbookLocalTime = JournalSchedule.EffectiveTime(member.WeekbookLocalTime),
            EffectiveMonthbookLocalTime = JournalSchedule.EffectiveTime(member.MonthbookLocalTime),
            EffectiveWeekStartsOn = JournalSchedule.EffectiveWeekStart(member.JournalWeekStartsOn),

            TimeZoneId = timeZone.Id,

            EarliestSelectableTime = JournalSchedule.EarliestLocalTime,
            LatestSelectableTime = JournalSchedule.LatestLocalTime,
            StepMinutes = JournalSchedule.StepMinutes,

            // Every book's generator now exists, so every timing on this screen governs something
            // that actually runs.
            WeekbookAvailable = true,
            MonthbookAvailable = true,
        };
    }
}
