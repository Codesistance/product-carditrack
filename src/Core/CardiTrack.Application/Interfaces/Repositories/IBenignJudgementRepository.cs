using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IBenignJudgementRepository : IRepository<BenignJudgement>
{
    /// <summary>
    /// The rules already judged benign for this member on any of <paramref name="localDates"/>.
    /// One read per member per pass rather than one per finding: a pass asks about at most a
    /// handful of rules and two dates, and the answer is the same set for all of them.
    /// </summary>
    Task<IReadOnlyCollection<(string Rule, DateOnly LocalDate)>> GetJudgedAsync(
        Guid cardiMemberId, IReadOnlyCollection<DateOnly> localDates, CancellationToken ct = default);

    /// <summary>
    /// Records one judgement, doing nothing if the same member, rule and local day is already
    /// recorded. Two overlapping assessor executions can both judge the same finding — the pass
    /// takes no claim — so losing that race is ordinary rather than an error.
    /// </summary>
    Task RecordAsync(BenignJudgement judgement, CancellationToken ct = default);

    /// <summary>Deletes judgements older than <paramref name="before"/>. Returns the row count.</summary>
    Task<int> DeleteOlderThanAsync(DateTime before, CancellationToken ct = default);
}
