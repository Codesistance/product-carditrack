using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IAlertRepository : IRepository<Alert>
{
    /// <summary>
    /// Alerts for one member, newest first. Tracked, so producers can resolve or inspect
    /// history in place. Pass <paramref name="activeOnly"/> false when the same-data / same
    /// episode check must still see a caregiver-deleted row.
    /// </summary>
    Task<IEnumerable<Alert>> GetByCardiMemberAsync(Guid cardiMemberId, bool activeOnly);

    /// <summary>
    /// Active, unresolved alerts, newest first. Read-only — producers that resolve alerts must
    /// keep using <see cref="GetByCardiMemberAsync"/> so the entities stay tracked.
    /// </summary>
    Task<IReadOnlyList<Alert>> GetUnresolvedByCardiMemberAsync(Guid cardiMemberId);

    /// <summary>
    /// Active alerts a caregiver can still open: every unresolved one, plus resolved ones raised
    /// since <paramref name="resolvedSince"/>. Newest first, read-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set <see cref="GetUnresolvedByCardiMemberAsync"/> returns is the set that is still
    /// <em>happening</em>. This is the set that can still be <em>read</em>, which is a different
    /// and larger thing: resolving an alert closes the episode without hiding the row, and the
    /// detail screen serves it for as long as it is active. Anything that has to act on what a
    /// caregiver may open — backfilling a missing explanation, most of all — needs this one, or a
    /// producer resolving an alert minutes after it fired silently ends the work.
    /// </para>
    /// <para>
    /// Bounded by a date rather than open-ended because the caller walks what comes back: the
    /// archive grows without limit, the set of episodes that are still live does not.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Alert>> GetServableByCardiMemberAsync(
        Guid cardiMemberId, DateTime resolvedSince);

    /// <summary>
    /// When this member's most recent alert was raised, or null when nothing has ever been raised
    /// about them. The anchor for <see cref="Services.QuietStretch"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately counts resolved <em>and</em> caregiver-deleted rows, unlike every other read
    /// here. Those flags say what is still worth showing on a list; this asks when something last
    /// happened, and an episode a caregiver swiped away still happened. Telling them "all quiet
    /// for 30 days" about a week they were called about would read as the app having lost track.
    /// </remarks>
    Task<DateTime?> GetLastTriggeredDateAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<Alert?> GetByIdWithCardiMemberAsync(Guid alertId);

    /// <summary>One page of alerts matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<Alert>> QueryAsync(AlertQuery query, CancellationToken ct = default);

    /// <summary>How many alerts match <paramref name="query"/>'s filters, ignoring its paging.</summary>
    Task<int> CountAsync(AlertQuery query, CancellationToken ct = default);

    /// <summary>
    /// Unacknowledged, unresolved alerts across <paramref name="cardiMemberIds"/> — the badge
    /// count, so deliberately unaffected by the caller's severity/date filters.
    /// </summary>
    Task<int> CountUnreadAsync(IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default);
}
