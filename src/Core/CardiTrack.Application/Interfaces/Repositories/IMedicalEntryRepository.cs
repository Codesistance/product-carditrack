using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>The lines of each member's medical information, current and past.</summary>
public interface IMedicalEntryRepository : IRepository<MedicalEntry>
{
    /// <summary>
    /// Every line on file for this member, current and removed, oldest first. Tracked: the ledger's
    /// writes load the member's lines and change them in place.
    /// </summary>
    Task<List<MedicalEntry>> GetByCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);
}
