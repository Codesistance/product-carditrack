using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class UserOrganizationRepository : Repository<UserOrganization>, IUserOrganizationRepository
{
    public UserOrganizationRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<UserOrganization>> GetByUserIdAsync(Guid userId)
    {
        // Read-only on every request that asks "which families" — the same reason the member-link
        // repository reads without tracking.
        return await _dbSet
            .AsNoTracking()
            .Where(uo => uo.UserId == userId)
            .ToListAsync();
    }

    public async Task<IEnumerable<UserOrganization>> GetByOrganizationIdAsync(Guid organizationId)
    {
        return await _dbSet
            .Where(uo => uo.OrganizationId == organizationId)
            .ToListAsync();
    }

    public async Task<UserOrganization?> GetAsync(Guid userId, Guid organizationId)
    {
        return await _dbSet
            .FirstOrDefaultAsync(uo => uo.UserId == userId && uo.OrganizationId == organizationId);
    }
}
