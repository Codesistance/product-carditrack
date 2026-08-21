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
    /// The pending question itself (if any), for callers that need more than
    /// <see cref="HasPendingAsync"/>'s yes/no — the Dashboard's badge, in particular, which cannot
    /// afford <see cref="GetByCardiMemberAsync"/>'s whole-history load on a screen that refreshes
    /// every 30 seconds. Same lapse rule as <see cref="HasPendingAsync"/>.
    /// </summary>
    Task<MemberQuestionnaire?> GetPendingAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default);

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

    /// <summary>
    /// Questions still waiting on a family that are due for a push — never yet pushed, or pushed
    /// more than <paramref name="reminderCutoffUtc"/> ago and still under
    /// <paramref name="maxPushes"/> — what <c>QuestionnaireAlertWorker</c>'s sweep reads.
    /// </summary>
    /// <param name="reminderCutoffUtc">
    /// A row last pushed at or before this instant is due again. The worker computes it as
    /// <c>utcNow - ReminderInterval</c>; kept as a plain instant here, like
    /// <see cref="GetLapsedPendingAsync"/>'s <paramref name="utcNow"/>, so this stays a pure filter
    /// with no clock or TimeSpan arithmetic of its own.
    /// </param>
    Task<IReadOnlyList<MemberQuestionnaire>> GetDueForAlertAsync(
        DateTime utcNow, DateTime reminderCutoffUtc, int maxPushes, int limit, CancellationToken ct = default);

    /// <summary>
    /// Claims one question for a push: a conditional update that only succeeds if
    /// <see cref="MemberQuestionnaire.ReminderCount"/> still matches what the sweep read it as. The
    /// same discipline as <c>NotificationRepository.TryClaimForPushAsync</c> — the Worker runs
    /// several instances, so a plain read-then-write here would risk a duplicate push.
    /// </summary>
    Task<bool> TryClaimAlertAsync(
        Guid questionnaireId, int expectedReminderCount, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Undoes a claim whose push failed after <see cref="TryClaimAlertAsync"/> succeeded — the same
    /// role <c>NotificationRepository.ReleasePushClaimAsync</c> plays. Without this, a transient
    /// send failure would still cost the question its next 24h-away reminder, since the claim's
    /// bump to <see cref="MemberQuestionnaire.ReminderCount"/>/<see cref="MemberQuestionnaire.LastRemindedAtUtc"/>
    /// already happened even though nothing was actually sent.
    /// </summary>
    Task ReleaseAlertClaimAsync(
        Guid questionnaireId, int claimedReminderCount, DateTime? previousLastRemindedAtUtc, CancellationToken ct = default);
}
