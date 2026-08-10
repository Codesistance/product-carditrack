using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Alerts;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <inheritdoc cref="IAlertSnapshotQueries"/>
public class AlertSnapshotQueries : IAlertSnapshotQueries
{
    /// <summary>
    /// Days of history loaded. Enough for the trend rule's two fortnights plus slack for missing
    /// days, and no more — this runs per member across the estate.
    /// </summary>
    private const int HistoryDays = 35;

    private readonly CardiTrackDbContext _context;
    private readonly IGranularMetricRepository _granular;

    public AlertSnapshotQueries(CardiTrackDbContext context, IGranularMetricRepository granular)
    {
        _context = context;
        _granular = granular;
    }

    public async Task<IReadOnlyList<Guid>> GetMembersDueForEvaluationAsync(
        DateOnly since, Guid? after, int take, CancellationToken ct = default)
    {
        var utcNow = DateTime.UtcNow;

        var query =
            from member in _context.CardiMembers
            where member.IsActive
                  && (member.MonitoringPausedUntil == null || member.MonitoringPausedUntil <= utcNow)
                  && _context.ActivityLogs.Any(a => a.CardiMemberId == member.Id && a.Date >= since)
            select member.Id;

        if (after.HasValue)
            query = query.Where(id => id.CompareTo(after.Value) > 0);

        return await query.OrderBy(id => id).Take(take).ToListAsync(ct);
    }

    public async Task<AlertContext?> BuildContextAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default)
    {
        var member = await _context.CardiMembers
            .FirstOrDefaultAsync(m => m.Id == cardiMemberId && m.IsActive, ct);

        if (member is null)
            return null;

        // The established baseline only. A provisional one describes a member still being learned,
        // and BaselineProgress is explicit that nothing may alert off it.
        var baseline = await _context.PatternBaselines
            .Where(b => b.CardiMemberId == cardiMemberId && b.PeriodDays >= BaselineProgress.PeriodDays)
            .OrderByDescending(b => b.CalculatedDate)
            .FirstOrDefaultAsync(ct);

        if (baseline is null)
            return null;

        var today = DateOnly.FromDateTime(utcNow);
        var from = today.AddDays(-HistoryDays);

        // Excludes today: the sync pulls the day in progress, and half a day of steps compared
        // against a whole day's baseline reads as inactivity every morning.
        var days = await _context.ActivityLogs
            .Where(a => a.CardiMemberId == cardiMemberId && a.Date >= from && a.Date < today)
            .OrderBy(a => a.Date)
            .Select(a => new AlertDaySnapshot
            {
                Date = a.Date,
                Steps = a.Steps,
                RestingHeartRate = a.RestingHeartRate,
                SleepEfficiency = a.SleepEfficiency,
                SleepMinutes = a.SleepMinutes,
                ActiveMinutes = a.ActiveMinutes
            })
            .ToListAsync(ct);

        var openTypes = await _context.Alerts
            .Where(a => a.CardiMemberId == cardiMemberId && a.IsActive && !a.IsResolved)
            .Select(a => a.AlertType)
            .Distinct()
            .ToListAsync(ct);

        var timeZoneId = await ResolveTimeZoneAsync(cardiMemberId, ct);
        var todayHours = await LoadTodayHoursAsync(cardiMemberId, timeZoneId, utcNow, ct);

        return new AlertContext
        {
            UtcNow = utcNow,
            CardiMemberId = cardiMemberId,
            Sensitivity = member.AlertSensitivity,
            TimeZoneId = timeZoneId,
            MonitoringPausedUntil = member.MonitoringPausedUntil,
            Baseline = new AlertBaselineSnapshot
            {
                PeriodDays = baseline.PeriodDays,
                AvgSteps = baseline.AvgSteps,
                StdDevSteps = baseline.StdDevSteps,
                AvgRestingHeartRate = baseline.AvgRestingHeartRate,
                StdDevHeartRate = baseline.StdDevHeartRate,
                AvgSleepMinutes = baseline.AvgSleepMinutes,
                AvgSleepEfficiency = baseline.AvgSleepEfficiency
            },
            RecentDays = days,
            TodayHours = todayHours,
            OpenAlertTypes = openTypes
        };
    }

    /// <summary>
    /// A CardiMember has no time zone of their own, so the primary caregiver's stands in — they
    /// are the person the phrase "this morning" is being reported to. Falls back to <c>"UTC"</c>,
    /// which suppresses the time-of-day rule rather than guessing.
    /// </summary>
    private async Task<string> ResolveTimeZoneAsync(Guid cardiMemberId, CancellationToken ct)
    {
        var timeZone = await (
            from link in _context.UserCardiMembers
            join user in _context.Users on link.UserId equals user.Id
            where link.CardiMemberId == cardiMemberId && link.IsActive && user.IsActive
            orderby link.IsPrimaryCaregiver descending, link.AssignedDate
            select user.TimeZoneId).FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone;
    }

    /// <summary>
    /// Today's step totals per local hour. Returns empty when the member's zone is unknown or no
    /// sub-daily data exists — and the morning rule treats empty as "we cannot tell", never as
    /// "they did not move".
    /// </summary>
    private async Task<IReadOnlyList<AlertHourSnapshot>> LoadTodayHoursAsync(
        Guid cardiMemberId, string timeZoneId, DateTime utcNow, CancellationToken ct)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
            return [];

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var localMidnight = localNow.Date;

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, zone);

        // Whole hours both ends, which GetWindowAsync requires and GetRollupsAsync assumes.
        var toUtc = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
        if (toUtc <= fromUtc)
            return [];

        var rollups = await _granular.GetRollupsAsync(
            cardiMemberId, GranularMetric.Steps, fromUtc, toUtc, ct);

        return
        [
            .. rollups
                .Select(r => new AlertHourSnapshot
                {
                    LocalHour = TimeZoneInfo.ConvertTimeFromUtc(r.HourStartUtc, zone).Hour,
                    // Steps is a counter, so the hour's total is its sum, not its average.
                    Steps = (int)Math.Round(r.Sum)
                })
                .OrderBy(h => h.LocalHour)
        ];
    }
}
