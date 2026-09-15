using System.Collections.Concurrent;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// The in-memory <see cref="IGeneratedContentSchedule"/>. A singleton, because outliving the
/// page is the entire point of it.
/// </summary>
/// <remarks>
/// <para>
/// Memory only, deliberately. These timestamps are a courtesy to the network, not a promise: a
/// cold start has nothing to go on and reads everything once, which is the right answer for a
/// screen nobody has looked at this session anyway. Nothing here is worth writing to the device,
/// and what it holds — member ids and clock readings — is not something to leave on disk.
/// </para>
/// <para>
/// Dropped when the session generation moves. <see cref="SessionGeneration"/> advances on every
/// sign-in and sign-out, so the next caregiver on a shared phone does not inherit the last one's
/// timings, and does not have their member ids sitting in a dictionary either.
/// </para>
/// <para>
/// Bounded by how many CardiMembers a caregiver has, times the three cards, so it is left to
/// grow rather than swept: a caregiver with a dozen members holds a few dozen DateTimes.
/// </para>
/// </remarks>
public sealed class GeneratedContentSchedule : IGeneratedContentSchedule
{
    private readonly SessionGeneration _session;
    private readonly Func<DateTime> _utcNow;
    /// <summary>
    /// Every entry carries the session it was written in, and a reader ignores one from any
    /// other. That, rather than the check in <see cref="Record"/>, is what actually closes the
    /// door: the check and the write cannot be made atomic between them, so a read that passed
    /// the check can still land after a sign-out has emptied the dictionary and reinsert itself.
    /// Stamped, such an entry is inert when it lands — the next reader does not recognise it, and
    /// the next <see cref="DropIfSessionChanged"/> takes it away.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid Member, GeneratedCard Card), (int Session, DateTime At)> _lastRead = new();

    private int _generation;

    /// <param name="utcNow">
    /// The clock, for the tests. One constructor with one defaulted parameter rather than two
    /// overloads: the container resolves <see cref="SessionGeneration"/> and fills this in from
    /// the default, where a second public constructor would leave it choosing between them.
    /// </param>
    public GeneratedContentSchedule(SessionGeneration session, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _generation = session.Current;
    }

    public bool IsDue(Guid cardiMemberId, GeneratedCard card, bool requestedByCaregiver)
    {
        DropIfSessionChanged();

        var session = _session.Current;
        var lastRead = _lastRead.TryGetValue((cardiMemberId, card), out var entry) && entry.Session == session
            ? entry.At
            : DateTime.MinValue;

        // Read again after the lookup. A sign-out between the two would otherwise let the
        // previous caregiver's entry match the snapshot above and answer "not due" for the one
        // who replaced them — and their API cache was wiped at sign-out, so the peek behind that
        // answer finds nothing and the card sits empty. When in doubt, due: a read nobody needed
        // costs a request, and the alternative costs a caregiver the summary.
        if (_session.Current != session)
            return true;

        return GeneratedContentRefresh.IsDue(requestedByCaregiver, lastRead, _utcNow());
    }

    public int CurrentSession => _session.Current;

    public void Record(Guid cardiMemberId, GeneratedCard card, int session)
    {
        DropIfSessionChanged();

        // Checked after the drop, not before it. The drop only notices that the session moved
        // since this object last looked; by the time a read from a signed-out caregiver lands,
        // the caregiver who replaced them has usually looked already, so the schedule is current
        // and the drop has nothing to do. What is stale is the read, and only the session it
        // started in can say so.
        //
        // This turns a stale read away at the door; the session stamped on the entry is what
        // handles the one that gets past — nothing can hold the check and the write together, so
        // a read that passed here can still be overtaken by a sign-out before it writes.
        if (session != _session.Current)
            return;

        // Never lowers the session on an entry. Between the check above and this write a newer
        // session can have recorded the same card, and a plain assignment would put the older
        // one back — after which every reader in the new session ignores it and re-reads. The
        // sessions only ever increase, so "keep the higher" is the whole rule.
        var now = _utcNow();
        _lastRead.AddOrUpdate(
            (cardiMemberId, card),
            (session, now),
            (_, existing) => existing.Session > session ? existing : (session, now));
    }

    /// <summary>
    /// Clears everything the moment the session behind it is not the one it was filled under.
    /// Checked on the way in rather than hooked to sign-out, so there is no subscription to
    /// unwire and no ordering to get wrong: the first question asked after a sign-out is the one
    /// that empties it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moves the mark forward only. A plain exchange lets a caller that sampled the session,
    /// then paused across a sign-in, write its stale reading back — which drops the mark below
    /// where the new session left it. The generation is monotonic
    /// (<see cref="SessionGeneration.Advance"/> only increments), so refusing to move it
    /// backwards is enough and needs no lock.
    /// </para>
    /// <para>
    /// The clear itself is housekeeping, not the guard. It is outside the compare-exchange, so a
    /// caller paused between the two can wipe entries a later session has since written. Nothing
    /// is riding on that: what makes a stale entry harmless is the session stamped on it, and
    /// what a lost clear costs is one read of a card whose timestamp went missing. The failure is
    /// one-sided by construction — clearing can only make a card look due, never make a stale one
    /// look fresh — which is why this is left unsynchronised rather than given a lock it would
    /// hold on the UI thread.
    /// </para>
    /// </remarks>
    private void DropIfSessionChanged()
    {
        var current = _session.Current;

        while (true)
        {
            var seen = Volatile.Read(ref _generation);
            if (seen >= current)
                return;

            if (Interlocked.CompareExchange(ref _generation, current, seen) == seen)
            {
                _lastRead.Clear();
                return;
            }
        }
    }
}
