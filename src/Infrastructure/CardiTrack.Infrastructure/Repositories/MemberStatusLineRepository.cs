using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberStatusLineRepository : Repository<MemberStatusLine>, IMemberStatusLineRepository
{
    public MemberStatusLineRepository(CardiTrackDbContext context) : base(context)
    {
    }

    // Deliberately tracked (no AsNoTracking): the batch writer reads-then-updates the same row,
    // and the API's read pays nothing measurable for tracking one entity per request.
    public Task<MemberStatusLine?> GetByCardiMemberAsync(Guid cardiMemberId) =>
        _dbSet.FirstOrDefaultAsync(s => s.CardiMemberId == cardiMemberId);
}
