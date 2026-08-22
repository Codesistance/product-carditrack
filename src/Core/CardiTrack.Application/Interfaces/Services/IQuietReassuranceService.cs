namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// One pass of the all-clear sweep: find the members nothing has been raised about for long
/// enough that the silence is worth reporting, and tell their families so.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="IStatisticalAlertService"/>, and deliberately built the same
/// way — same candidate filter, same per-member isolation, same "the pipeline must actually have
/// been running" precondition. A product that only ever speaks up about bad news leaves every
/// quiet week ambiguous: the family cannot tell "we looked and they are fine" from "nobody is
/// looking". This is what closes that gap, and <c>QuietStretch</c> is what keeps it honest.
/// </remarks>
public interface IQuietReassuranceService
{
    /// <summary>
    /// Evaluates every candidate member once; returns how many members have an all-clear standing
    /// with their family for the current week. Not a send count: the sweep runs daily so a member
    /// crossing the line is told that day, and the days after that re-resolve the same deduped
    /// delivery rather than pushing again.
    /// </summary>
    Task<int> SweepAsync(DateTime utcNow, CancellationToken ct = default);
}
