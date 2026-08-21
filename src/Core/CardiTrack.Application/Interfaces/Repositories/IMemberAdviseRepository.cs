using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMemberAdviseRepository : IRepository<MemberAdvise>
{
    /// <summary>The member's current suggestion, or null when no batch has generated one yet.
    /// Tracked, because the one writer (the batch regeneration) updates it in place.</summary>
    Task<MemberAdvise?> GetByCardiMemberAsync(Guid cardiMemberId);
}
