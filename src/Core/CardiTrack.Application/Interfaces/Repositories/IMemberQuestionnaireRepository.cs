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
    /// <param name="utcNow">
    /// The clock a question is judged askable against. A row that has lapsed
    /// (<see cref="MemberQuestionnaire.HasLapsed"/>) is not one this family is being asked, whatever
    /// its status column still says — without this, one question nobody got to before its day ended
    /// would gag the feature for that member until the sweep next ran.
    /// </param>
    Task<bool> HasPendingAsync(Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// When this member's family was last asked anything at all — answered, dismissed or still
    /// waiting. The second noise gate: a minimum interval measured from the asking, so declining to
    /// answer does not invite another question the next day.
    /// </summary>
    Task<DateTime?> GetLatestGeneratedAtAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Questions still waiting on a family that have outlived the moment they asked about, across
    /// every member — what the expiry sweep retires.
    /// </summary>
    /// <param name="limit">
    /// Ceiling on one pass. The sweep is a background tidy-up, not a deadline: a bounded batch keeps
    /// one long outage's backlog from turning into a single unbounded write transaction.
    /// </param>
    Task<IReadOnlyList<MemberQuestionnaire>> GetLapsedPendingAsync(
        DateTime utcNow, int limit, CancellationToken ct = default);
}
