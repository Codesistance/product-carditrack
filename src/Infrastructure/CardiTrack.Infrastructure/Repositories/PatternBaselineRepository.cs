using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class PatternBaselineRepository : Repository<PatternBaseline>, IPatternBaselineRepository
{
    public PatternBaselineRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<PatternBaseline?> GetLatestByCardiMemberAsync(Guid cardiMemberId, int periodDays)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(pb => pb.CardiMemberId == cardiMemberId && pb.PeriodDays == periodDays)
            .OrderByDescending(pb => pb.CalculatedDate)
            .FirstOrDefaultAsync();
    }
}
