using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class OrganizationRepository : Repository<Organization>, IOrganizationRepository
{
    public OrganizationRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<Organization?> GetWithSubscriptionAsync(Guid id)
    {
        // Subscription is an ignored navigation (no mapped relationships on Organization),
        // so it cannot be Include()d — stitch it in with a second query instead.
        var organization = await _dbSet.FirstOrDefaultAsync(o => o.Id == id);
        if (organization == null) return null;

        organization.Subscription = await _context.Set<Subscription>()
            .FirstOrDefaultAsync(s => s.OrganizationId == id);

        return organization;
    }

    public async Task<int> DeleteOrphanedAsync(TimeSpan minAge)
    {
        // Age guard keeps any onboarding still in flight (org committed, user not
        // yet — only possible via the legacy two-call endpoints) out of scope.
        var cutoff = DateTime.UtcNow - minAge;
        return await _dbSet
            .Where(o => o.CreatedDate < cutoff
                && !_context.Users.Any(u => u.OrganizationId == o.Id)
                && !_context.CardiMembers.Any(c => c.OrganizationId == o.Id))
            .ExecuteDeleteAsync();
    }
}
