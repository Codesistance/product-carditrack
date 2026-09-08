using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// The per-member, per-purpose holds a generation path consults before spending a model call.
/// Every write here executes immediately rather than waiting for <see cref="IUnitOfWork.SaveChangesAsync"/>:
/// a hold is recorded on the failure path, where nothing else is being saved, and it must survive
/// whatever the caller does next.
/// </summary>
public interface IMemberAiHoldRepository
{
    Task<MemberAiHold?> GetAsync(Guid cardiMemberId, AiHoldPurpose purpose, CancellationToken ct = default);

    /// <summary>
    /// Writes the hold, replacing the member's existing row for the same purpose if there is one.
    /// Two overlapping pipeline executions can both record a failure for the same member; the
    /// later write wins and both describe the same event, so nothing is lost either way.
    /// </summary>
    Task UpsertAsync(MemberAiHold hold, CancellationToken ct = default);

    /// <summary>Removes the member's hold for the purpose, if any. A no-op when there is none.</summary>
    Task ClearAsync(Guid cardiMemberId, AiHoldPurpose purpose, CancellationToken ct = default);
}
