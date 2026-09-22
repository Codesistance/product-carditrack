using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

public interface IAlertService
{
    /// <summary>
    /// One page of alerts for the mobile Alerts List (M1-10), newest first.
    /// </summary>
    /// <param name="cardiMemberId">
    /// Narrows to a single member; null spans every member the user may read. Throws
    /// <see cref="KeyNotFoundException"/> when supplied but not readable by this user.
    /// </param>
    Task<AlertListResponse> GetAlertsAsync(
        Guid requestingUserId,
        Guid? cardiMemberId = null,
        AlertSeverity? severity = null,
        AlertStatusFilter? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// One alert for the detail screen. Throws <see cref="KeyNotFoundException"/> when the
    /// alert does not exist, is inactive, or belongs to a member the user may not read.
    /// </summary>
    Task<AlertDetailResponse> GetByIdAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Marks one alert as handled by the requesting user. Idempotent: acknowledging an
    /// already-acknowledged alert keeps the original timestamp and acknowledger rather than
    /// rewriting who dealt with it. Throws <see cref="KeyNotFoundException"/> when the alert
    /// doesn't exist or belongs to a member the user may not read.
    /// </summary>
    Task<AlertAcknowledgementResponse> AcknowledgeAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Acknowledges, and records what the caregiver said about it: a canned code from the alert's
    /// <c>responseOptions</c>, a line of their own, or both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alert's own first-wins acknowledgement is unchanged — this appends, it does not
    /// overwrite. A second caregiver answering an already-acknowledged alert keeps the first one's
    /// attribution and still has their say recorded, which is the point: with more than one person
    /// watching, "who is on this" and "what has been done" are different questions.
    /// </para>
    /// <para>
    /// Answering also halts the escalation ladder for this alert, which acknowledging in the app
    /// did not previously do — only a push ack did. Throws
    /// <see cref="Exceptions.AlertResponseCodeException"/> for a code this alert's rule does not
    /// offer, naming the ones it does.
    /// </para>
    /// </remarks>
    Task<AlertAcknowledgementResponse> AcknowledgeAsync(
        Guid requestingUserId, Guid alertId, string? responseCode, string? note,
        CancellationToken ct = default);

    /// <summary>
    /// Closes an alert on the family's behalf: it is dealt with, and here is what was done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closing resolves the alert, which is also what re-arms the producer's cooldown — one
    /// unresolved alert per rule suppresses that rule, so a condition that persists raises a fresh
    /// alert the next time it is evaluated rather than staying silent behind somebody's note. That
    /// is the whole reason close exists as a separate action from acknowledge.
    /// </para>
    /// <para>
    /// <strong>There is no undo.</strong> <see cref="UnacknowledgeAsync"/> exists because
    /// "handled" is a claim somebody can make in error and take back; a close says the episode is
    /// over, which is the same claim CardiTrack's own producers make when a condition passes, and
    /// they cannot take it back either. Reopening is what the next alert is for.
    /// </para>
    /// <para>
    /// Two caregivers closing within seconds is not an error: the first sets
    /// <see cref="Domain.Entities.Alert.ResolvedByUserId"/>, both responses are kept, and the
    /// second's result carries the first's attribution. An alert CardiTrack already resolved keeps
    /// its null resolver and still records the response.
    /// </para>
    /// </remarks>
    Task<AlertAcknowledgementResponse> CloseAsync(
        Guid requestingUserId, Guid alertId, string? responseCode, string? note,
        CancellationToken ct = default);

    /// <summary>
    /// Puts an acknowledged alert back to unhandled. "Handled" is a claim a caregiver makes about
    /// themselves, and they can be wrong about it — tapping the wrong row, or acknowledging on the
    /// way to doing something they then could not do. Without this the mistake is permanent and
    /// the alert is silently out of everyone's unread count.
    /// </summary>
    /// <remarks>
    /// Idempotent like its opposite, and refuses a <see cref="Domain.Entities.Alert.IsResolved"/>
    /// alert with <see cref="Exceptions.AlertStateException"/>: resolution is the system's
    /// judgement that the underlying condition has passed, and a caregiver toggle must not reopen
    /// it. Its own exception type rather than <see cref="InvalidOperationException"/> because the
    /// API returns the message to the caregiver verbatim — see the type's own remarks.
    /// </remarks>
    Task<AlertAcknowledgementResponse> UnacknowledgeAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Removes an alert from every list it would otherwise appear in — a caregiver's own
    /// housekeeping, distinct from <see cref="AcknowledgeAsync"/>'s "handled" record. Soft
    /// delete (<see cref="Domain.Entities.Alert.IsActive"/>), same pattern
    /// <c>CardiMemberService</c> uses for member removal. Throws
    /// <see cref="KeyNotFoundException"/> when the alert doesn't exist or belongs to a member
    /// the user may not manage.
    /// </summary>
    Task DeleteAsync(Guid requestingUserId, Guid alertId, CancellationToken ct = default);
}
