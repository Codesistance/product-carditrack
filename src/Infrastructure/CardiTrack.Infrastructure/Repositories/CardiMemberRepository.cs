using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class CardiMemberRepository : Repository<CardiMember>, ICardiMemberRepository
{
    public CardiMemberRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<CardiMember>> GetByOrganizationIdAsync(Guid organizationId)
    {
        return await _dbSet
            .Where(cm => cm.OrganizationId == organizationId && cm.IsActive)
            .ToListAsync();
    }

    public async Task<CardiMember?> GetWithRelationshipsAsync(Guid id)
    {
        // Navigations are unmapped by convention (see CardiMemberConfiguration.Ignore calls),
        // so relationships load via FK queries rather than Include.
        var member = await _dbSet.FirstOrDefaultAsync(cm => cm.Id == id);
        if (member is null)
        {
            return null;
        }

        member.UserCardiMembers = await _context.UserCardiMembers
            .Where(ucm => ucm.CardiMemberId == id)
            .ToListAsync();

        return member;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveIdsWithActivitySinceAsync(DateOnly since)
    {
        return await _dbSet
            .Where(cm => cm.IsActive &&
                _context.ActivityLogs.Any(al => al.CardiMemberId == cm.Id && al.Date >= since))
            .Select(cm => cm.Id)
            .ToListAsync();
    }
}
