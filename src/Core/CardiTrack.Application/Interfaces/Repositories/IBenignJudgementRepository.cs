using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IBenignJudgementRepository : IRepository<BenignJudgement>
{
    /// <summary>
    /// The fingerprints of the findings already judged benign for this member on any of
    /// <paramref name="localDates"/>. One read per member per pass rather than one per finding: a
    /// pass asks about at most a handful of findings and two dates, and the answer is the same set
    /// for all of them.
    /// </summary>
    /// <remarks>
    /// A fingerprint already carries the rule and the figures behind it
    /// (<c>StatisticalAlertRules.JudgementFingerprint</c>), so it is the whole key the caller
    /// matches on; the dates are here to bound the read, not to disambiguate it.
    /// </remarks>
    Task<IReadOnlyCollection<string>> GetJudgedFingerprintsAsync(
        Guid cardiMemberId, IReadOnlyCollection<DateOnly> localDates, CancellationToken ct = default);

    /// <summary>
    /// Records one judgement, doing nothing if the same member, rule, local day and finding
    /// fingerprint is already recorded. Two overlapping assessor executions can both judge the
    /// same finding — the pass takes no claim — so losing that race is ordinary rather than an
    /// error.
    /// </summary>
    Task RecordAsync(BenignJudgement judgement, CancellationToken ct = default);

    /// <summary>Deletes judgements older than <paramref name="before"/>. Returns the row count.</summary>
    Task<int> DeleteOlderThanAsync(DateTime before, CancellationToken ct = default);
}
