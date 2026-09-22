using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class RhythmEpisodeRepository : IRhythmEpisodeRepository
{
    private readonly CardiTrackDbContext _context;

    public RhythmEpisodeRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task<bool> UpsertAsync(RhythmEpisode episode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // Claim-then-update, the same shape RealtimeAssessmentRepository uses and for the same
        // hard reason: PostgreSQL refuses system columns in RETURNING on a partitioned table, so
        // the `RETURNING (xmax = 0)` that would decide insert-versus-update in one statement
        // cannot run here at all. It does not degrade — it throws, and with the caller treating
        // episode writes as best-effort that would have meant every beat silently discarded while
        // the day's counts looked healthy.
        var claimed = await _context.Database.SqlQuery<int>($"""
            INSERT INTO "RhythmEpisodes"
                ("CardiMemberId", "DeviceConnectionId", "WindowStartUtc", "WindowEndUtc",
                 "NotificationStartUtc", "Positive", "BeatCount", "RrMilliseconds",
                 "OffsetMillisFromStart", "MeanRrMs", "MinRrMs", "MaxRrMs", "RmssdMs",
                 "IngestedAtUtc")
            VALUES ({episode.CardiMemberId}, {episode.DeviceConnectionId}, {episode.WindowStartUtc},
                    {episode.WindowEndUtc}, {episode.NotificationStartUtc}, {episode.Positive},
                    {episode.BeatCount}, {episode.RrMilliseconds}, {episode.OffsetMillisFromStart},
                    {episode.MeanRrMs}, {episode.MinRrMs}, {episode.MaxRrMs}, {episode.RmssdMs},
                    {episode.IngestedAtUtc})
            ON CONFLICT ("CardiMemberId", "DeviceConnectionId", "WindowStartUtc") DO NOTHING
            RETURNING 1 AS "Value"
            """).ToListAsync(ct);

        if (claimed.Count > 0)
            return true;

        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RhythmEpisodes" SET
                "WindowEndUtc" = {episode.WindowEndUtc},
                "NotificationStartUtc" = {episode.NotificationStartUtc},
                "Positive" = {episode.Positive},
                "BeatCount" = {episode.BeatCount},
                "RrMilliseconds" = {episode.RrMilliseconds},
                "OffsetMillisFromStart" = {episode.OffsetMillisFromStart},
                "MeanRrMs" = {episode.MeanRrMs},
                "MinRrMs" = {episode.MinRrMs},
                "MaxRrMs" = {episode.MaxRrMs},
                "RmssdMs" = {episode.RmssdMs},
                "IngestedAtUtc" = {episode.IngestedAtUtc}
            WHERE "CardiMemberId" = {episode.CardiMemberId}
              AND "DeviceConnectionId" = {episode.DeviceConnectionId}
              AND "WindowStartUtc" = {episode.WindowStartUtc}
            """, ct);

        return false;
    }

    public async Task<IReadOnlyList<RhythmEpisode>> GetInRangeAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        return await _context.RhythmEpisodes
            .AsNoTracking()
            .Where(e => e.CardiMemberId == cardiMemberId
                        && e.WindowStartUtc >= fromUtc
                        && e.WindowStartUtc < toUtc)
            .OrderBy(e => e.WindowStartUtc)
            .ToListAsync(ct);
    }
}
