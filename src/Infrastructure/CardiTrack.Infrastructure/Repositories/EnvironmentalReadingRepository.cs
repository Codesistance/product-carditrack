using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the partitioned environmental-reading table: raw
/// <c>ON CONFLICT</c> upsert (the natural key is the idempotency key), LINQ reads over the
/// composite index — the same shape as <c>RealtimeAssessmentRepository</c>.
/// </summary>
public class EnvironmentalReadingRepository : IEnvironmentalReadingRepository
{
    private readonly CardiTrackDbContext _context;

    public EnvironmentalReadingRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task<bool> UpsertAsync(EnvironmentalReading reading, CancellationToken ct = default)
    {
        var claimed = await _context.Database.SqlQuery<int>($"""
            INSERT INTO "EnvironmentalReadings"
                ("CardiMemberId", "SessionStartUtc", "SessionEndUtc", "DeviceConnectionId",
                 "TemperatureCelsius", "AirQualityIndex", "AirQualityCategory",
                 "WeatherCondition", "RelativeHumidityPercent", "GeneratedAtUtc")
            VALUES ({reading.CardiMemberId}, {reading.SessionStartUtc}, {reading.SessionEndUtc},
                    {reading.DeviceConnectionId}, {reading.TemperatureCelsius},
                    {reading.AirQualityIndex}, {reading.AirQualityCategory},
                    {reading.WeatherCondition}, {reading.RelativeHumidityPercent}, {reading.GeneratedAtUtc})
            ON CONFLICT ("CardiMemberId", "SessionStartUtc") DO NOTHING
            RETURNING 1 AS "Value"
            """).ToListAsync(ct);

        if (claimed.Count > 0)
            return true;

        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EnvironmentalReadings" SET
                "SessionEndUtc" = {reading.SessionEndUtc},
                "DeviceConnectionId" = {reading.DeviceConnectionId},
                "TemperatureCelsius" = {reading.TemperatureCelsius},
                "AirQualityIndex" = {reading.AirQualityIndex},
                "AirQualityCategory" = {reading.AirQualityCategory},
                "WeatherCondition" = {reading.WeatherCondition},
                "RelativeHumidityPercent" = {reading.RelativeHumidityPercent},
                "GeneratedAtUtc" = {reading.GeneratedAtUtc}
            WHERE "CardiMemberId" = {reading.CardiMemberId}
              AND "SessionStartUtc" = {reading.SessionStartUtc}
            """, ct);
        return false;
    }

    public async Task<bool> ExistsAsync(
        Guid cardiMemberId, DateTime sessionStartUtc, CancellationToken ct = default)
    {
        return await _context.EnvironmentalReadings
            .AsNoTracking()
            .AnyAsync(r => r.CardiMemberId == cardiMemberId && r.SessionStartUtc == sessionStartUtc, ct);
    }

    public async Task<EnvironmentalReading?> GetLatestAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        return await _context.EnvironmentalReadings
            .AsNoTracking()
            .Where(r => r.CardiMemberId == cardiMemberId)
            .OrderByDescending(r => r.SessionStartUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<EnvironmentalReading>> GetOverlappingAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Overlap, not containment: a session that started before local midnight and ended after
        // it belongs to both days it touched.
        return await _context.EnvironmentalReadings
            .AsNoTracking()
            .Where(r => r.CardiMemberId == cardiMemberId
                        && r.SessionStartUtc < toUtc
                        && r.SessionEndUtc >= fromUtc)
            .OrderBy(r => r.SessionStartUtc)
            .ToListAsync(ct);
    }
}
