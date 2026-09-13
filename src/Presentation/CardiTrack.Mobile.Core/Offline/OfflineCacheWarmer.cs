using System.Collections.Concurrent;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// The push-triggered half of cache-first reads: when a notification arrives, pull every
/// default GET into the offline cache so the caregiver who opens the app next is not kept
/// waiting on a screen the device has never answered.
/// </summary>
/// <remarks>
/// Each GET already writes its body through <see cref="ICardiTrackApiClient"/>; this type
/// only decides which questions to ask. Failures are swallowed per call — one unreachable
/// digest must not cost the dashboard. The run itself never faults, so a fire-and-forget
/// from the push handler cannot become an unobserved exception.
/// </remarks>
public sealed class OfflineCacheWarmer : IOfflineCacheWarmer
{
    private readonly ICardiTrackApiClient _api;
    private readonly ITokenStore _tokens;
    private readonly ILogger<OfflineCacheWarmer> _logger;
    private readonly object _gate = new();
    private Task? _inFlight;
    private CancellationTokenSource? _runCts;
    private bool _signingOut;

    public OfflineCacheWarmer(
        ICardiTrackApiClient api,
        ITokenStore tokens,
        ILogger<OfflineCacheWarmer>? logger = null)
    {
        _api = api;
        _tokens = tokens;
        _logger = logger ?? NullLogger<OfflineCacheWarmer>.Instance;
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_signingOut)
                return Task.CompletedTask;

            if (_inFlight is { IsCompleted: false } running)
                return running;

            // The shared run is not tied to the caller's token: a page going away, or a
            // second trigger cancelling, must not abort a warm a push already started.
            // Sign-out is the exception — DrainForSignOutAsync cancels this source so a
            // late GET cannot write the previous caregiver's answers after the wipe.
            var runCts = new CancellationTokenSource();
            _runCts = runCts;
            var run = RunAndReleaseAsync(runCts);
            _inFlight = run;
            return run;
        }
    }

    public async Task DrainForSignOutAsync(CancellationToken ct = default)
    {
        Task? running;
        CancellationTokenSource? runCts;
        lock (_gate)
        {
            _signingOut = true;
            running = _inFlight;
            runCts = _runCts;
        }

        try
        {
            runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed the source between the lock and here.
        }

        if (running is null)
            return;

        try
        {
            await running.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The run stopped, or the sign-out caller gave up. Either way the wipe proceeds.
        }
    }

    public void ResumeAfterSignOut()
    {
        lock (_gate)
            _signingOut = false;
    }

    private async Task RunAndReleaseAsync(CancellationTokenSource runCts)
    {
        try
        {
            await RefreshCoreAsync(runCts.Token);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runCts, runCts))
                    _runCts = null;
            }

            runCts.Dispose();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken runCt)
    {
        try
        {
            if (await _tokens.GetAsync() is null)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            timeout.CancelAfter(OfflineReadDefaults.WarmTimeout);
            var ct = timeout.Token;

            var members = await Safe(() => _api.GetCardiMembersAsync(ct), ct) ?? [];
            var alertIds = new ConcurrentBag<Guid>();

            var jobs = new List<Func<CancellationToken, Task>>(AccountJobs(alertIds));
            foreach (var member in members)
                jobs.AddRange(MemberJobs(member.Id, alertIds));

            await RunAllAsync(jobs, ct);

            var details = alertIds
                .Distinct()
                .Take(OfflineReadDefaults.AlertDetailLimit)
                .Select(id => (Func<CancellationToken, Task>)(c => _api.GetAlertAsync(id, c)))
                .ToList();
            await RunAllAsync(details, ct);
        }
        catch (OperationCanceledException)
        {
            // Timed out or the process is going away — the next open retries what is missing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache warm failed.");
        }
    }

    private List<Func<CancellationToken, Task>> AccountJobs(ConcurrentBag<Guid> alertIds) =>
    [
        ct => _api.GetOnboardingStatusAsync(ct),
        ct => _api.GetNotificationSummaryAsync(ct),
        ct => _api.GetNotificationsAsync(state: nameof(NotificationState.Open), ct: ct),
        ct => _api.GetNotificationPreferencesAsync(ct),
        ct => _api.GetNotificationMutesAsync(ct),
        ct => _api.GetAlarmCatalogueAsync(ct),
        ct => _api.GetExportConsentsAsync(ct),
        ct => CaptureAlerts(
            () => _api.GetAlertsAsync(status: OfflineReadDefaults.OpenAlertStatus, ct: ct),
            alertIds, ct),
    ];

    private List<Func<CancellationToken, Task>> MemberJobs(Guid memberId, ConcurrentBag<Guid> alertIds) =>
    [
        ct => _api.GetCardiMemberAsync(memberId, ct),
        ct => _api.GetDashboardAsync(memberId, ct),
        ct => _api.GetCurrentStatusAsync(memberId, ct),
        ct => _api.GetDigestAsync(memberId, ct),
        ct => _api.GetAdviseAsync(memberId, ct),
        ct => _api.GetDevicesAsync(memberId, ct),
        ct => _api.GetAlertPreferencesAsync(memberId, ct),
        ct => _api.GetMemberAlarmsAsync(memberId, ct),
        ct => _api.GetJournalSettingsAsync(memberId, ct),
        ct => _api.GetJournalEntriesAsync(
            memberId, JournalCadence.Daybook, OfflineReadDefaults.JournalHistoryLimit, ct: ct),
        ct => _api.GetJournalEntriesAsync(
            memberId, JournalCadence.Weekbook, OfflineReadDefaults.JournalHistoryLimit, ct: ct),
        ct => _api.GetJournalEntriesAsync(
            memberId, JournalCadence.Monthbook, OfflineReadDefaults.JournalHistoryLimit, ct: ct),
        ct => _api.GetQuestionnairesAsync(
            memberId,
            search: null,
            OfflineReadDefaults.QuestionnairePage,
            OfflineReadDefaults.QuestionnairePageSize,
            ct),
        ct => CaptureAlerts(
            () => _api.GetAlertsAsync(
                status: OfflineReadDefaults.OpenAlertStatus, cardiMemberId: memberId, ct: ct),
            alertIds, ct),
        ct => _api.GetCurrentMemberChatSessionAsync(memberId, ct),
        ct => _api.GetMemberChatSessionsAsync(memberId, ct),
        ct => _api.GetMemberChatSuggestionsAsync(memberId, ct),
    ];

    private async Task CaptureAlerts(
        Func<Task<AlertListResponse>> call, ConcurrentBag<Guid> alertIds, CancellationToken ct)
    {
        var page = await Safe(call, ct);
        if (page is null)
            return;

        foreach (var alert in page.Alerts)
            alertIds.Add(alert.AlertId);
    }

    private async Task RunAllAsync(IReadOnlyList<Func<CancellationToken, Task>> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0)
            return;

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = OfflineReadDefaults.MaxConcurrency,
                CancellationToken = ct,
            },
            async (job, token) => await Safe(() => job(token), token));
    }

    private async Task<T?> Safe<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // GetAsync wraps an abort as ApiException. Either form means the run was
            // cancelled — stop, do not treat it as one missing answer among others.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            // A 404 on digest (no summary yet) and a 5xx on chat are the same to a warm: that
            // one answer is missing, everything else still lands.
            _logger.LogDebug(ex, "Offline cache warm skipped one read.");
            return default;
        }
    }

    private async Task Safe(Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Offline cache warm skipped one read.");
        }
    }
}
