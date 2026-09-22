using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// What caregivers said about an alert (<see cref="AlertResponse"/>). Append-only — there is no
/// update or delete here, and the absence is the contract: the record of who did what about an
/// alert is not something a later reader gets to tidy.
/// </summary>
public interface IAlertResponseRepository : IRepository<AlertResponse>
{
    /// <summary>Every response on one alert, newest first.</summary>
    Task<IReadOnlyList<AlertResponse>> GetForAlertAsync(Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Every response across a set of alerts, newest first — the list screen's version of
    /// <see cref="GetForAlertAsync"/>, so a page of alerts costs one query rather than one each.
    /// </summary>
    Task<IReadOnlyList<AlertResponse>> GetForAlertsAsync(
        IReadOnlyCollection<Guid> alertIds, CancellationToken ct = default);
}
