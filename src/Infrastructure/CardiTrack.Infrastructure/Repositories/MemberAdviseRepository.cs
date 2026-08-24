using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberAdviseRepository : Repository<MemberAdvise>, IMemberAdviseRepository
{
    public MemberAdviseRepository(CardiTrackDbContext context) : base(context)
    {
    }

    // Deliberately tracked (no AsNoTracking): the batch writer reads-then-updates the same rows,
    // and the API's read pays nothing measurable for tracking a handful of entities per request.
    public async Task<IReadOnlyList<MemberAdvise>> GetAllByCardiMemberAsync(Guid cardiMemberId) =>
        await _dbSet.Where(a => a.CardiMemberId == cardiMemberId).ToListAsync();
}
