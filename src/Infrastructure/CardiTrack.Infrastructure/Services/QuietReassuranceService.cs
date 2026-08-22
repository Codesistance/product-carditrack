using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IQuietReassuranceService"/>
public class QuietReassuranceService : IQuietReassuranceService
{
    /// <summary>
    /// The candidate filter, identical to <see cref="StatisticalAlertService"/>'s: active members
    /// with a reading in the last two days. Shared on purpose — the members this sweep may
    /// reassure have to be a subset of the members that engine is actually watching, and two
    /// filters would eventually disagree about which those are.
    /// </summary>
    private const int CandidateLookbackDays = 2;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IDispatchService _dispatch;
    private readonly ILogger<QuietReassuranceService> _logger;

    public QuietReassuranceService(
        IUnitOfWork unitOfWork,
        IDispatchService dispatch,
        ILogger<QuietReassuranceService> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatch = dispatch;
        _logger = logger;
    }

    public async Task<int> SweepAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var since = DateOnly.FromDateTime(utcNow).AddDays(-CandidateLookbackDays);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var reassured = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ReassureMemberAsync(memberId, utcNow, ct))
                    reassured++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest of the fleet this pass — the same
                // isolation StatisticalAlertService keeps, and cheaper here: a missed all-clear
                // is a family who hears nothing this week, which is the status quo this feature
                // exists to improve rather than a regression.
                _logger.LogError(ex, "Quiet-reassurance evaluation failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        // "Standing", not "sent": most days this counts members whose all-clear for the week
        // already went out and was deduped, not fresh pushes. Reading it as a send count would
        // make a healthy estate look like it was pushing daily.
        _logger.LogInformation(
            "Quiet-reassurance pass complete. Candidates: {Candidates}, members with a standing all-clear: {Reassured}.",
            memberIds.Count, reassured);
        return reassured;
    }

    /// <returns>Whether this member has an all-clear standing with their family for the current
    /// week; false for every member the silence has not earned it for, which on any given day is
    /// most of them.</returns>
    private async Task<bool> ReassureMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive)
            return false;

        // Established 30-day baseline only, the same gate StatisticalAlertService applies before
        // it will raise anything. It is the load-bearing precondition of this whole feature: a
        // member with no established baseline has no rule running against them, so their silence
        // is not evidence of anything and must never be reported as though it were.
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);

        var unresolved = await _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(memberId);
        var lastAlertAt = await _unitOfWork.Alerts.GetLastTriggeredDateAsync(memberId, ct);

        // Freshness from the same two inputs and the same function the dashboard reads, so the
        // card a caregiver opens after this push cannot be captioned "no data in over 12 hours"
        // by a screen that computed staleness its own way.
        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(memberId)).ToList();
        var lastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate);
        var latestAssessment = await _unitOfWork.RealtimeAssessments.GetLatestAsync(memberId, ct);
        var (freshnessTier, _) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt, latestAssessment?.GeneratedAtUtc, utcNow, string.Empty);

        var quiet = QuietStretch.Evaluate(new QuietStretchContext
        {
            UtcNow = utcNow,
            IsMonitoringPaused = member.IsMonitoringPaused(utcNow),
            HasEstablishedBaseline = baseline is not null,
            HasUnresolvedAlerts = unresolved.Count > 0,
            DataFreshnessTier = freshnessTier,
            LastAlertAtUtc = lastAlertAt,
            MonitoringSinceUtc = member.CreatedDate,
        });

        if (!quiet.IsQuiet)
            return false;

        // The occurrence is what paces this to one push a week — see EnqueueForReassuranceAsync.
        // The sweep itself runs daily precisely so a member crossing the seven-day line is told
        // that day rather than on whichever weekday a weekly job happened to land on.
        var deliveries = await _dispatch.EnqueueForReassuranceAsync(
            memberId, QuietStretch.WeeklyOccurrence(quiet.Days), ct);

        // Zero means nobody on this member is set to receive alerts — ordinary, and not worth
        // counting. A non-empty result does *not* mean a push went out today: on the second and
        // later days of a week the dedup key resolves to the row already sent and comes back
        // unchanged, which is exactly how the cadence is held. So this counts members standing
        // under an all-clear this week, not sends — see the log line in SweepAsync. Nothing is
        // claimed either way: the key is derived, so a failed pass consumes nothing.
        return deliveries.Count > 0;
    }
}
