using CardiTrack.Application.Services.Alerts;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Builds the pre-fetched snapshots the statistical alert rules evaluate against.
/// </summary>
public interface IAlertSnapshotQueries
{
    /// <summary>
    /// Active, unpaused members with a device that has reported recently — the only ones a rule
    /// could say anything about.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetMembersDueForEvaluationAsync(
        DateOnly since, Guid? after, int take, CancellationToken ct = default);

    /// <summary>
    /// One member's snapshot: established baseline, recent whole days, today's hourly steps in
    /// their local clock, and which alert types are already open for them.
    /// </summary>
    Task<AlertContext?> BuildContextAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default);
}
