using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The R1 statistical alert engine (docs/execution/backend/api/alerts.md): every 15 minutes,
/// each recently-active member's daily readings are evaluated against their established
/// 30-day baseline by the pure rules in <see cref="StatisticalAlertRules"/>. Fetching the
/// 30-day baseline and nothing else is how "provisional baselines never alert" is enforced —
/// members without an established baseline are skipped wholesale.
/// <para>
/// Two layers keep a 15-minute cadence from paging anyone twice: the rule-scoped cooldown
/// (<see cref="AlertRuleMarkers.Suppresses"/> — one unresolved alert per remedy) and a
/// same-local-day dedup (a daily-grain rule that fired and was resolved must not re-fire from
/// the same day's data that evening).
/// </para>
/// </summary>
public class StatisticalAlertService : IStatisticalAlertService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<StatisticalAlertService> _logger;

    public StatisticalAlertService(IUnitOfWork unitOfWork, ILogger<StatisticalAlertService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> EvaluateAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // The established candidate filter: active members with data in the last two days.
        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var raised = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                raised += await EvaluateMemberAsync(memberId, utcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest of the fleet this pass.
                _logger.LogError(ex, "Statistical alert evaluation failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Statistical alert pass complete. Candidates: {Candidates}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    private async Task<int> EvaluateMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return 0;

        // Established baseline only: no 30-day baseline means every rule stays silent, exactly
        // as the provisional-never-alerts principle demands.
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);
        if (baseline is null)
            return 0;

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localToday = DateOnly.FromDateTime(localNow);
        var yesterday = localToday.AddDays(-1);

        // One fetch covers every rule: yesterday and today for the daily rules, four trailing
        // weeks for the trend. Stored dates are the wearer's civil days.
        var logsByDate = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                memberId, localToday.AddDays(-7 * StatisticalAlertRules.TrendWeeks), localToday))
            .ToDictionary(l => l.Date);
        var yesterdayLog = logsByDate.GetValueOrDefault(yesterday);
        var todayLog = logsByDate.GetValueOrDefault(localToday);

        var candidates = new[]
        {
            StatisticalAlertRules.ActivityDecline(baseline, yesterdayLog),
            StatisticalAlertRules.IrregularSleep(baseline, yesterdayLog),
            StatisticalAlertRules.ElevatedHeartRate(baseline, yesterdayLog),
            StatisticalAlertRules.NoMorningActivity(baseline, todayLog, localNow),
            StatisticalAlertRules.LongTermTrend(logsByDate, yesterday),
        }.OfType<StatisticalAlertCandidate>().ToList();
        if (candidates.Count == 0)
            return 0;

        var existing = (await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: true)).ToList();

        var raised = 0;
        foreach (var candidate in candidates)
        {
            if (existing.Any(a => AlertRuleMarkers.Suppresses(a, candidate.Type, candidate.Rule)))
                continue;

            // Same-local-day dedup, regardless of resolution state: a rule reads one day's
            // data, so one day gets at most one alert from it — resolving at noon must not
            // re-page at half past from the same readings.
            if (existing.Any(a => AlertRuleMarkers.HasRule(a, candidate.Rule)
                    && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(a.TriggeredDate, timeZone)) == localToday))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            await _unitOfWork.Alerts.AddAsync(new Alert
            {
                CardiMemberId = memberId,
                AlertType = candidate.Type,
                Severity = candidate.Severity,
                Title = candidate.Title,
                Message = candidate.Message,
                TriggeredDate = utcNow,
                MetricValues = candidate.MetricValues,
            });
            raised++;
        }

        if (raised > 0)
            await _unitOfWork.SaveChangesAsync();
        return raised;
    }
}
