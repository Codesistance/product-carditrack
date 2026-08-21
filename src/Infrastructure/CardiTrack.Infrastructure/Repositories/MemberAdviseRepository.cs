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

    // Deliberately tracked (no AsNoTracking): the batch writer reads-then-updates the same row,
    // and the API's read pays nothing measurable for tracking one entity per request.
    public Task<MemberAdvise?> GetByCardiMemberAsync(Guid cardiMemberId) =>
        _dbSet.FirstOrDefaultAsync(a => a.CardiMemberId == cardiMemberId);
}
