using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the questionnaire table. An ordinary EF-tracked table, unlike the
/// partitioned derived-data tables beside it: these rows are written one at a time by a person
/// answering, are edited afterwards, and are deleted outright on request.
/// </summary>
public class MemberQuestionnaireRepository : Repository<MemberQuestionnaire>, IMemberQuestionnaireRepository
{
    public MemberQuestionnaireRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<MemberQuestionnaire>> GetByCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(q => q.CardiMemberId == cardiMemberId)
            .OrderByDescending(q => q.GeneratedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<bool> HasPendingAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default)
    {
        // HasLapsed's condition, written out: EF has to translate this into SQL, and a call to a
        // method on the entity is not something it can translate.
        return await _dbSet
            .AsNoTracking()
            .AnyAsync(
                q => q.CardiMemberId == cardiMemberId
                     && q.Status == QuestionnaireStatus.Pending
                     && (q.AskableUntilUtc == null || q.AskableUntilUtc > utcNow),
                ct);
    }

    public async Task<IReadOnlyList<MemberQuestionnaire>> GetLapsedPendingAsync(
        DateTime utcNow, int limit, CancellationToken ct = default)
    {
        // Tracked, unlike everything else here: the caller's next move is to retire these rows, and
        // a no-tracking read would only have to be attached again to do it.
        return await _dbSet
            .Where(q => q.Status == QuestionnaireStatus.Pending
                        && q.AskableUntilUtc != null
                        && q.AskableUntilUtc <= utcNow)
            .OrderBy(q => q.AskableUntilUtc)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<DateTime?> GetLatestGeneratedAtAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        // Max over a nullable projection rather than ordering and taking one: no row yields null,
        // which is the "never asked" answer the caller wants, without a second existence check.
        return await _dbSet
            .AsNoTracking()
            .Where(q => q.CardiMemberId == cardiMemberId)
            .MaxAsync(q => (DateTime?)q.GeneratedAtUtc, ct);
    }
}
