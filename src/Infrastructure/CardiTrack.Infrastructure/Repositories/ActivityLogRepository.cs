using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class ActivityLogRepository : Repository<ActivityLog>, IActivityLogRepository
{
    public ActivityLogRepository(CardiTrackDbContext context) : base(context)
    {
    }

    /// <summary>
    /// Writes one row per CardiMember per day. The match must use the same key as the
    /// unique index on (CardiMemberId, Date) — keying the lookup on DeviceConnectionId
    /// instead would miss the existing row for a member's second device and insert a
    /// duplicate that the index then rejects.
    /// </summary>
    public async Task UpsertAsync(ActivityLog log)
    {
        var existing = await _dbSet
            .FirstOrDefaultAsync(al => al.CardiMemberId == log.CardiMemberId
                                       && al.Date == log.Date);

        if (existing is null)
        {
            await _dbSet.AddAsync(log);
        }
        else
        {
            // Record which device supplied the day, so a switch of source device is visible.
            existing.DeviceConnectionId = log.DeviceConnectionId;
            existing.DataSource = log.DataSource;

            existing.Steps = log.Steps;
            existing.Distance = log.Distance;
            existing.ActiveMinutes = log.ActiveMinutes;
            existing.SedentaryMinutes = log.SedentaryMinutes;
            existing.Floors = log.Floors;
            existing.CaloriesBurned = log.CaloriesBurned;
            existing.RestingHeartRate = log.RestingHeartRate;
            existing.AvgHeartRate = log.AvgHeartRate;
            existing.MaxHeartRate = log.MaxHeartRate;
            existing.MinHeartRate = log.MinHeartRate;
            existing.SleepMinutes = log.SleepMinutes;
            existing.SleepStartTime = log.SleepStartTime;
            existing.SleepEndTime = log.SleepEndTime;
            existing.SleepEfficiency = log.SleepEfficiency;
            existing.DeepSleepMinutes = log.DeepSleepMinutes;
            existing.LightSleepMinutes = log.LightSleepMinutes;
            existing.RemSleepMinutes = log.RemSleepMinutes;
            existing.AwakeMinutes = log.AwakeMinutes;
            existing.SpO2Average = log.SpO2Average;
            existing.SpO2Min = log.SpO2Min;
            existing.SpO2Max = log.SpO2Max;
            existing.VO2Max = log.VO2Max;
            existing.StressScore = log.StressScore;
            existing.BreathingRate = log.BreathingRate;
            existing.Temperature = log.Temperature;

            // TemperatureBaseline and TemperatureVariation were missing from this update list: a day
            // row created before them, or revised after, kept whatever the two columns already held
            // while every neighbouring column moved. The dashboard's temperature card reads the
            // baseline to say whether a night was unusual, so the stale pair could out-argue the fresh
            // reading beside it. Fixed here rather than filed, because the sweep that added the
            // columns below would otherwise have copied the same omission nine more times.
            existing.TemperatureBaseline = log.TemperatureBaseline;
            existing.TemperatureVariation = log.TemperatureVariation;

            existing.HeartRateVariabilityMs = log.HeartRateVariabilityMs;
            existing.WeightKg = log.WeightKg;
            existing.BloodGlucoseAverage = log.BloodGlucoseAverage;
            existing.BloodGlucoseMin = log.BloodGlucoseMin;
            existing.BloodGlucoseMax = log.BloodGlucoseMax;
            existing.EcgReadings = log.EcgReadings;
            existing.EcgAtrialFibrillationReadings = log.EcgAtrialFibrillationReadings;
            existing.IrregularRhythmNotifications = log.IrregularRhythmNotifications;
            _dbSet.Update(existing);
        }
    }

    public async Task<IEnumerable<ActivityLog>> GetByCardiMemberAndDateRangeAsync(
        Guid cardiMemberId, DateOnly from, DateOnly to)
    {
        // Read-only for every caller (dashboard, insights, alerting). Upsert looks the row up
        // itself, so these entities must not enter the tracker — a 30-day dashboard poll would
        // otherwise attach ~30 wide rows on every tick.
        return await _dbSet
            .AsNoTracking()
            .Where(al => al.CardiMemberId == cardiMemberId
                         && al.Date >= from
                         && al.Date <= to)
            .OrderBy(al => al.Date)
            .ToListAsync();
    }
}
