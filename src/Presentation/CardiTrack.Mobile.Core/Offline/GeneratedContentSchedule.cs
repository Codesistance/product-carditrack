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
    private readonly ConcurrentDictionary<(Guid Member, GeneratedCard Card), DateTime> _lastRead = new();

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

        var lastRead = _lastRead.TryGetValue((cardiMemberId, card), out var at) ? at : DateTime.MinValue;
        return GeneratedContentRefresh.IsDue(requestedByCaregiver, lastRead, _utcNow());
    }

    public void Record(Guid cardiMemberId, GeneratedCard card)
    {
        DropIfSessionChanged();
        _lastRead[(cardiMemberId, card)] = _utcNow();
    }

    /// <summary>
    /// Clears everything the moment the session behind it is not the one it was filled under.
    /// Checked on the way in rather than hooked to sign-out, so there is no subscription to
    /// unwire and no ordering to get wrong: the first question asked after a sign-out is the one
    /// that empties it.
    /// </summary>
    private void DropIfSessionChanged()
    {
        var current = _session.Current;
        if (Interlocked.Exchange(ref _generation, current) != current)
            _lastRead.Clear();
    }
}
