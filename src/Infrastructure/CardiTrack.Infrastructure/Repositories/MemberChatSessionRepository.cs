using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberChatSessionRepository : Repository<MemberChatSession>, IMemberChatSessionRepository
{
    public MemberChatSessionRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<MemberChatSession?> GetActiveAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default)
    {
        // Tracked, not AsNoTracking: the caller's next move is almost always to append a turn and
        // bump LastTurnAtUtc on this same instance.
        return await _dbSet
            .Where(s => s.UserId == userId
                        && s.CardiMemberId == cardiMemberId
                        && s.LastTurnAtUtc >= activeSinceUtc)
            .OrderByDescending(s => s.LastTurnAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<MemberChatSession?> GetByIdWithTurnsAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Include(s => s.Turns.OrderBy(t => t.CreatedAtUtc))
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<IReadOnlyList<MemberChatSessionListing>> ListForMemberAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default)
    {
        // Projected, not Included: the list needs one turn's content and a count per session, and
        // Include would drag every encrypted turn of every conversation across the wire to show a
        // one-line summary of each.
        return await _dbSet
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.CardiMemberId == cardiMemberId)
            .OrderByDescending(s => s.LastTurnAtUtc)
            .Select(s => new MemberChatSessionListing
            {
                Session = s,
                FirstQuestionContent = s.Turns
                    .Where(t => t.Role == ChatTurnRole.User)
                    .OrderBy(t => t.CreatedAtUtc)
                    .Select(t => t.Content)
                    .FirstOrDefault(),
                QuestionCount = s.Turns.Count(t => t.Role == ChatTurnRole.User),
            })
            .ToListAsync(ct);
    }
}
