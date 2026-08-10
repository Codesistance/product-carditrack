using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The device-silence failsafe (docs/llm_design.md `InactivityDetector`). Every generated
/// artifact in the pipeline deliberately refuses to speak from silence — the digest skips,
/// the assessor skips — so a watch that dies would otherwise produce nothing at all. This
/// pass turns that nothing into exactly one yellow alert.
/// <para>
/// Silence means <b>no granular readings</b>, not "no successful sync": a sync that completes
/// and returns no new minutes is precisely the dead-battery / watch-on-the-nightstand case
/// this alert exists to catch. And it only counts during the member's waking hours, on their
/// anchor clock — overnight silence is a charging watch, not an emergency.
/// </para>
/// </summary>
public class InactivityDetectionService : IInactivityDetectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InactivityDetectionService> _logger;

    public InactivityDetectionService(IUnitOfWork unitOfWork, ILogger<InactivityDetectionService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<int> DetectAsync(
        DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct = default)
    {
        // Nonsense rules must fail loud, not quietly misfire: a zero threshold would alert on
        // every gap between samples, and inverted waking hours would silence the check forever.
        if (rules.SilenceThresholdMinutes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(rules), rules.SilenceThresholdMinutes, "SilenceThresholdMinutes must be positive.");
        if (rules.WakingStartHour < 0 || rules.WakingEndHour > 24 || rules.WakingStartHour >= rules.WakingEndHour)
            throw new ArgumentOutOfRangeException(
                nameof(rules), $"{rules.WakingStartHour}..{rules.WakingEndHour}",
                "Waking hours must satisfy 0 <= start < end <= 24.");

        // The same candidate filter as the digest and the assessor: active members with data in
        // the last two days. A member silent for longer has aged out of candidacy — and already
        // has their standing alert from the pass that watched them go quiet. A brand-new
        // connection that has never produced data never qualifies; there is nothing to miss yet.
        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var raised = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await CheckMemberAsync(memberId, utcNow, rules, ct))
                    raised++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure: swallowing this would log a spurious error
                // and stumble through one more loop iteration instead of stopping cleanly.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest of the fleet this pass; the next
                // 15-minute run retries naturally.
                _logger.LogError(ex, "Inactivity check failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Inactivity detection complete. Candidates: {Candidates}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    private async Task<bool> CheckMemberAsync(
        Guid memberId, DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return false;

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);

        // The whole silence window must sit inside waking hours, so alerting effectively starts
        // at wakingStart + threshold (09:00 on the defaults). At 07:30 the trailing two hours
        // are mostly night — flagging a watch that is still on its charger would make the very
        // first alert of the day a false one, and the first one is the one that sets trust.
        var minutesOfDay = localNow.Hour * 60 + localNow.Minute;
        if (minutesOfDay < rules.WakingStartHour * 60 + rules.SilenceThresholdMinutes
            || minutesOfDay >= rules.WakingEndHour * 60)
        {
            return false;
        }

        var lastDataUtc = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);
        if (lastDataUtc is not null && lastDataUtc > utcNow.AddMinutes(-rules.SilenceThresholdMinutes))
            return false;

        // Cooldown: one unresolved inactivity alert at a time — a dead device would otherwise
        // re-page every 15 minutes. Resolving the alert is what re-arms the check.
        var existing = await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: true);
        if (existing.Any(a => a.AlertType == AlertType.Inactivity && !a.IsResolved))
            return false;

        var silentSince = lastDataUtc is null
            ? "for several hours"
            : $"since {TimeZoneInfo.ConvertTimeFromUtc(lastDataUtc.Value, timeZone):HH:mm}";
        await _unitOfWork.Alerts.AddAsync(new Alert
        {
            CardiMemberId = memberId,
            AlertType = AlertType.Inactivity,
            Severity = AlertSeverity.Yellow,
            Title = "Device has gone quiet",
            Message = $"No readings from the device {silentSince}. "
                      + "It may need charging, or a quick check that it is being worn.",
            TriggeredDate = utcNow,
            MetricValues = JsonSerializer.Serialize(new
            {
                lastDataUtc,
                thresholdMinutes = rules.SilenceThresholdMinutes,
            }),
        });
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// The end of the member's most recent minute with any granular reading at all, or null if
    /// the fetched range holds none. The range covers the silence threshold plus an hour of
    /// slack (whole-hour bounds, per the granular read contract) — precision beyond "silent
    /// longer than the threshold" changes nothing here.
    /// </summary>
    private async Task<DateTime?> LastGranularMinuteAsync(
        Guid memberId, DateTime utcNow, int thresholdMinutes, CancellationToken ct)
    {
        // Rounded-up threshold + 1 hour: `utcNow` sits at most an hour before `rangeEnd`, so
        // this is the tightest whole-hour range that always contains the threshold boundary —
        // data old enough to fall before it is older than the threshold by construction.
        var rangeEnd = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc)
            .AddHours(1);
        var rangeStart = rangeEnd.AddHours(-((thresholdMinutes + 59) / 60 + 1));
        var window = await _unitOfWork.GranularMetrics.GetWindowAsync(memberId, rangeStart, rangeEnd, ct);

        var lastIndex = -1;
        foreach (var series in window.MinuteSeries.Values)
        {
            var index = Array.FindLastIndex(series, v => v.HasValue);
            if (index > lastIndex)
                lastIndex = index;
        }

        return lastIndex >= 0 ? rangeStart.AddMinutes(lastIndex + 1) : null;
    }
}
