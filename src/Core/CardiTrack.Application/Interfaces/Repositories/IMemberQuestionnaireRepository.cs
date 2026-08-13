using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Access to the questions the service has asked a member's family, and their answers.
/// </summary>
public interface IMemberQuestionnaireRepository : IRepository<MemberQuestionnaire>
{
    /// <summary>Every question asked about this member, newest first, whatever became of it.</summary>
    Task<IReadOnlyList<MemberQuestionnaire>> GetByCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Whether a question is already waiting on this family. The first of the two noise gates: one
    /// open question at a time, so a member who has not answered is never asked a second thing.
    /// </summary>
    Task<bool> HasPendingAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// When this member's family was last asked anything at all — answered, dismissed or still
    /// waiting. The second noise gate: a minimum interval measured from the asking, so declining to
    /// answer does not invite another question the next day.
    /// </summary>
    Task<DateTime?> GetLatestGeneratedAtAsync(Guid cardiMemberId, CancellationToken ct = default);
}
