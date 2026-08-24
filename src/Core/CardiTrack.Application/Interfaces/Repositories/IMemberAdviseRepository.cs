using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMemberAdviseRepository : IRepository<MemberAdvise>
{
    /// <summary>The member's current topic-scoped suggestions — zero to one row per
    /// <see cref="CardiTrack.Domain.Enums.AdviseTopic"/>. Tracked, because the one writer (the
    /// batch regeneration) updates rows in place. Callers pick with <c>AdvisePicker</c> so every
    /// reader selects the same way.</summary>
    Task<IReadOnlyList<MemberAdvise>> GetAllByCardiMemberAsync(Guid cardiMemberId);
}
