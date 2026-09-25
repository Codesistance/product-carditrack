using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// An ordinary EF-tracked table: a member has a handful of lines, written one at a time by a
/// caregiver, so the whole ledger is read and rewritten in memory.
/// </summary>
public class MedicalEntryRepository : Repository<MedicalEntry>, IMedicalEntryRepository
{
    public MedicalEntryRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<List<MedicalEntry>> GetByCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(e => e.CardiMemberId == cardiMemberId)
            .OrderBy(e => e.AddedAtUtc)
            .ThenBy(e => e.CreatedDate)
            .ToListAsync(ct);
    }
}
