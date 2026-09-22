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

        // `xmax = 0` is true only on a row this statement inserted: an updated row carries the
        // locking transaction's id there. It is the one way to tell insert from update in a single
        // round trip, and the alternative — probe then write — races the concurrent pulls of two
        // connections belonging to the same member.
        var inserted = await _context.Database.SqlQuery<bool>($"""
            INSERT INTO "RhythmEpisodes"
                ("CardiMemberId", "WindowStartUtc", "WindowEndUtc", "DeviceConnectionId",
                 "NotificationStartUtc", "Positive", "BeatCount", "RrMilliseconds",
                 "OffsetMillisFromStart", "MeanRrMs", "MinRrMs", "MaxRrMs", "RmssdMs",
                 "IngestedAtUtc")
            VALUES ({episode.CardiMemberId}, {episode.WindowStartUtc}, {episode.WindowEndUtc},
                    {episode.DeviceConnectionId}, {episode.NotificationStartUtc}, {episode.Positive},
                    {episode.BeatCount}, {episode.RrMilliseconds}, {episode.OffsetMillisFromStart},
                    {episode.MeanRrMs}, {episode.MinRrMs}, {episode.MaxRrMs}, {episode.RmssdMs},
                    {episode.IngestedAtUtc})
            ON CONFLICT ("CardiMemberId", "WindowStartUtc") DO UPDATE SET
                "WindowEndUtc" = EXCLUDED."WindowEndUtc",
                "DeviceConnectionId" = EXCLUDED."DeviceConnectionId",
                "NotificationStartUtc" = EXCLUDED."NotificationStartUtc",
                "Positive" = EXCLUDED."Positive",
                "BeatCount" = EXCLUDED."BeatCount",
                "RrMilliseconds" = EXCLUDED."RrMilliseconds",
                "OffsetMillisFromStart" = EXCLUDED."OffsetMillisFromStart",
                "MeanRrMs" = EXCLUDED."MeanRrMs",
                "MinRrMs" = EXCLUDED."MinRrMs",
                "MaxRrMs" = EXCLUDED."MaxRrMs",
                "RmssdMs" = EXCLUDED."RmssdMs",
                "IngestedAtUtc" = EXCLUDED."IngestedAtUtc"
            RETURNING ("xmax" = 0) AS "Value"
            """).ToListAsync(ct);

        return inserted.FirstOrDefault();
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
