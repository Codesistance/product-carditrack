using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMemberStatusLineRepository : IRepository<MemberStatusLine>
{
    /// <summary>The member's current line, or null when no batch has generated one yet.
    /// Tracked, because the one writer (the batch regeneration) updates it in place.</summary>
    Task<MemberStatusLine?> GetByCardiMemberAsync(Guid cardiMemberId);
}
