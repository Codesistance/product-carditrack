using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceActivityLogRepository : Repository<DeviceActivityLog>, IDeviceActivityLogRepository
{
    public DeviceActivityLogRepository(CardiTrackDbContext context) : base(context)
    {
    }

    /// <summary>
    /// Writes a device's day, leaving the row untouched when the provider reported nothing new.
    /// </summary>
    /// <remarks>
    /// Deliberately no <c>_dbSet.Update(existing)</c> call: the entity is already tracked by the
    /// query above, so assigning the fields lets EF's change detection decide what actually
    /// differs. Marking the whole entity Modified instead would stamp <c>UpdatedDate</c> on every
    /// sync (see <c>CardiTrackDbContext.UpdateTimestamps</c>), which reduces that column to "when
    /// we last polled". It has to mean "when the provider's numbers last changed" — that is the
    /// signal sync cadence is calibrated from, and re-fetching a trailing window means most
    /// upserts legitimately change nothing.
    /// </remarks>
    public async Task UpsertAsync(DeviceActivityLog log)
    {
        var existing = await _dbSet
            .FirstOrDefaultAsync(al => al.DeviceConnectionId == log.DeviceConnectionId
                                       && al.Date == log.Date);

        if (existing is null)
        {
            await _dbSet.AddAsync(log);
            return;
        }

        existing.CardiMemberId = log.CardiMemberId;
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
        // column below would otherwise have copied the same omission again.
        existing.TemperatureBaseline = log.TemperatureBaseline;
        existing.TemperatureVariation = log.TemperatureVariation;

        existing.HeartRateVariabilityMs = log.HeartRateVariabilityMs;
        existing.OvernightBreathingRate = log.OvernightBreathingRate;
        existing.LightZoneMinutes = log.LightZoneMinutes;
        existing.ModerateZoneMinutes = log.ModerateZoneMinutes;
        existing.VigorousZoneMinutes = log.VigorousZoneMinutes;
        existing.PeakZoneMinutes = log.PeakZoneMinutes;
        existing.ModerateZoneFloorBpm = log.ModerateZoneFloorBpm;
        existing.LongestSedentaryStretchMinutes = log.LongestSedentaryStretchMinutes;
        existing.LongestSedentaryStretchStartUtc = log.LongestSedentaryStretchStartUtc;
    }

    public async Task<IEnumerable<DeviceActivityLog>> GetByCardiMemberAndDateAsync(
        Guid cardiMemberId, DateOnly date)
    {
        return await _dbSet
            .Where(al => al.CardiMemberId == cardiMemberId && al.Date == date)
            .ToListAsync();
    }
}
