using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// <see cref="IMemberWriteGuard"/> over Cloud SQL: <c>FOR KEY SHARE</c> on the member row, held for
/// the write's transaction.
/// </summary>
/// <remarks>
/// <para>
/// Written against the <see cref="DbContext"/> rather than through a repository, the same
/// deliberate exception <see cref="MemberErasureService"/> makes: what this does is take a row
/// lock, which is not something a repository contract can express and not something EF will issue
/// on its own.
/// </para>
/// <para>
/// One statement per member, in ascending id order, rather than one <c>= ANY(…)</c> statement over
/// all of them. A single statement would be cheaper and would not reliably lock in a fixed order:
/// PostgreSQL acquires row locks during the scan, so <c>ORDER BY</c> does not decide the order they
/// are taken in, and two concurrent report exports naming the same two members could each hold one
/// and wait for the other. Reports name a handful of members at most, so the extra round trips cost
/// nothing worth having a deadlock for.
/// </para>
/// </remarks>
public class MemberWriteGuard : IMemberWriteGuard
{
    private readonly CardiTrackDbContext _context;
    private readonly ILogger<MemberWriteGuard> _logger;

    public MemberWriteGuard(CardiTrackDbContext context, ILogger<MemberWriteGuard> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> WriteIfMembersLiveAsync(
        IReadOnlyCollection<Guid> cardiMemberIds,
        Func<CancellationToken, Task> write,
        CancellationToken ct = default)
    {
        if (cardiMemberIds.Count == 0)
            throw new ArgumentException("A guarded write must name the member it describes.", nameof(cardiMemberIds));

        // Joins an open transaction rather than nesting one, the way DigestRepository.ReplaceBookAsync
        // does. The chat turn reaches its save with the journal rung's transaction already open, and
        // the lock has to be taken inside whichever transaction the write commits in — a lock in a
        // transaction of its own would be released before the write it was protecting landed.
        var owns = _context.Database.CurrentTransaction is null;
        var transaction = owns ? await _context.Database.BeginTransactionAsync(ct) : null;
        try
        {
            foreach (var cardiMemberId in cardiMemberIds.Distinct().OrderBy(id => id))
            {
                if (await LockAsync(cardiMemberId, ct))
                    continue;

                _logger.LogWarning(
                    "Refused a generated write for CardiMember {CardiMemberId}: the member was erased "
                    + "while it was being generated. Nothing was written.",
                    cardiMemberId);

                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None);

                // Whatever the caller staged before calling is dropped too, not just left unsaved:
                // the generation is abandoned, and a tracked-but-unsaved entity would otherwise be
                // picked up by the next SaveChanges in this scope and written after all. A caller
                // that joined its own transaction still owns rolling that back — this only clears
                // what EF is holding in memory.
                _context.ChangeTracker.Clear();
                return false;
            }

            await write(ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return true;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<bool> HoldMembersAsync(
        IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default)
    {
        if (cardiMemberIds.Count == 0)
            throw new ArgumentException("A hold must name the member it protects.", nameof(cardiMemberIds));

        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "HoldMembersAsync needs a transaction already open: the lock lives for the length of "
                + "one, and taken outside it would be released before the work it was protecting "
                + "ran. Open the transaction first, then call this as its first statement.");
        }

        foreach (var cardiMemberId in cardiMemberIds.Distinct().OrderBy(id => id))
        {
            if (await LockAsync(cardiMemberId, ct))
                continue;

            _logger.LogWarning(
                "Refused to continue a transaction for CardiMember {CardiMemberId}: the member was "
                + "erased. The caller is expected to roll back.",
                cardiMemberId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes <c>FOR KEY SHARE</c> on one member row. False when the row is not there — either it
    /// never was, or an erasure that this call waited on has just committed.
    /// </summary>
    /// <remarks>
    /// <c>FOR KEY SHARE</c> rather than <c>FOR SHARE</c> or a plain read. A plain read is the whole
    /// bug (see <see cref="IMemberWriteGuard"/>): it takes no lock and cannot see the uncommitted
    /// delete, so it says yes to a member who is halfway gone. <c>FOR KEY SHARE</c> is the weakest
    /// lock that still conflicts with the <c>FOR UPDATE</c> the erasure takes — it is precisely
    /// what a foreign key would take on this row, which is the guarantee the schema gave up when it
    /// chose Guid references — and being the weakest, two generators writing for the same member
    /// never wait on each other.
    /// </remarks>
    private async Task<bool> LockAsync(Guid cardiMemberId, CancellationToken ct)
    {
        var live = await _context.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM "CardiMembers" WHERE "Id" = {cardiMemberId} FOR KEY SHARE
            """).ToListAsync(ct);
        return live.Count > 0;
    }
}
