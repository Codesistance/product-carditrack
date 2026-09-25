using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Generic sync service that works with any IDeviceApiClient implementation.
/// Register one instance per data-source API using .NET keyed services keyed on HealthApi;
/// a connection reaches its engine through the configured DeviceType→HealthApi mapping.
/// </summary>
public class DeviceSyncService : IDeviceSyncService
{
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly IDeviceApiClient _deviceApi;
    private readonly IDeviceConnectionRepository _deviceConnections;
    private readonly IDeviceActivityLogRepository _deviceActivityLogs;
    private readonly IActivityLogAggregationService _aggregation;
    private readonly IGranularIngestionService _granularIngestion;
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationGapResolver _gapResolver;
    private readonly List<DeviceProviderSettings> _providers;

    /// <summary>
    /// Optional so the tests that construct this service directly need not stand one up — the same
    /// reason <c>StatisticalAlertService</c> takes its insight service that way. Used only to
    /// report enrichment that failed without costing the sync.
    /// </summary>
    private readonly ILogger<DeviceSyncService>? _logger;

    /// <summary>
    /// The clock "today" is read from. Defaults to <see cref="TimeProvider.System"/>; tests pin it
    /// to put a member either side of UTC midnight, which is the whole behaviour under test.
    /// </summary>
    private readonly TimeProvider _clock;

    public DeviceSyncService(
        IOAuthTokenRefreshService tokenRefresh,
        IDeviceApiClient deviceApi,
        IDeviceConnectionRepository deviceConnections,
        IDeviceActivityLogRepository deviceActivityLogs,
        IActivityLogAggregationService aggregation,
        IGranularIngestionService granularIngestion,
        IUnitOfWork unitOfWork,
        INotificationGapResolver gapResolver,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<DeviceSyncService>? logger = null,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _tokenRefresh = tokenRefresh;
        _deviceApi = deviceApi;
        _deviceConnections = deviceConnections;
        _deviceActivityLogs = deviceActivityLogs;
        _aggregation = aggregation;
        _granularIngestion = granularIngestion;
        _unitOfWork = unitOfWork;
        _gapResolver = gapResolver;
        _providers = providers.Value;
    }

    public async Task SyncCardiMemberAsync(DeviceConnection connection, SyncScope scope = SyncScope.Routine)
    {
        var providerConfig = ResolveProviderConfig(connection);

        // RefreshIfExpiredAsync marks the connection itself when the provider refuses the grant;
        // letting the failure propagate is what gets it logged against this connection by the
        // caller.
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        // Read once and passed down, rather than each step asking the clock again. The token
        // refresh above is network I/O, so a sync that starts just before the member's midnight
        // can reach the pull on the following day: two independent reads would then decide "no
        // repair pass needed" against the old day and fetch against the new one, skipping that
        // day's backfill entirely — and the next pull, now stamped with the new day, would not
        // make it up. The success stamp below is held to this same day for the same reason.
        var (zone, today) = await MemberTodayAsync(connection);

        // The trailing repair days are re-fetched once a member-local day, not on every pull. They
        // exist to catch a provider revising a *finished* day, which happens on the order of hours
        // — paying for them every ten minutes would spend the whole per-user quota re-reading
        // numbers that cannot have moved. Today is pulled every time, which is the part a
        // caregiver sees. Local rather than UTC so the pass lands on the first pull after the day
        // it repairs has actually closed for the wearer; on UTC it fired mid-evening west of
        // Greenwich and re-read a day still in progress, leaving that evening for the next pass.
        var needsRepairPass = connection.LastSyncDate is not { } last
            || LocalDate(last, zone) != today;
        var lookbackDays = needsRepairPass ? Math.Max(1, providerConfig.SyncLookbackDays) : 0;

        try
        {
            // One-time identity capture: the provider's public health-user id is what maps a
            // webhook notification back to this connection, and the OAuth connect flow predates
            // the column — capturing here self-heals every existing connection on its next pull.
            // Best-effort by design: a wearer without one still syncs, they just cannot be
            // addressed by webhooks until the provider exposes it.
            if (connection.HealthUserId is null)
            {
                var healthUserId = await _deviceApi.GetHealthUserIdAsync(accessToken);
                if (healthUserId is not null)
                {
                    await _deviceConnections.UpdateHealthUserIdAsync(connection.Id, healthUserId);
                    connection.HealthUserId = healthUserId;
                }
            }

            await CaptureBatteryAsync(connection, accessToken);

            await PullWindowAsync(connection, accessToken, lookbackDays, today);

            // Only once the whole window landed — otherwise a partial sync would look complete
            // and the connection would not come due again until the next interval. This also
            // clears a SyncError left by an earlier run: the window just landed, so whatever
            // the provider was doing then, the connection is working now.
            await _deviceConnections.MarkSyncSucceededAsync(
                connection.Id, SuccessStamp(_clock.GetUtcNow().UtcDateTime, today, zone));

            // The worker-cadence extras run after the routine window succeeded, never inside its
            // success envelope: both are enrichment, and a transient failure in either must not
            // un-succeed the daily data that already landed — that would re-fetch the whole
            // window next pull and could park a working connection in SyncError over a series
            // the caregiver never sees directly. Scoped to the worker cadence because the
            // manual-sync path shares this method, and a caregiver waiting on a refresh must not
            // pay for a chunk of last month or five extra series.
            if (scope == SyncScope.WorkerCadence)
            {
                await IngestGranularWindowAsync(connection, accessToken, lookbackDays, today);
                await ClassifyRecentNightsAsync(connection, today);
                await BackfillHistoryAsync(connection, accessToken, providerConfig, today);
            }
        }
        catch (Exception ex) when (IsProviderApiException(ex))
        {
            await _deviceConnections.UpdateStatusAsync(connection.Id, ConnectionStatus.SyncError);
            throw;
        }
    }

    public async Task AuditSyncAsync(DeviceConnection connection)
    {
        var providerConfig = ResolveProviderConfig(connection);
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        // Never narrower than the routine window, or the audit could see less than the thing it
        // exists to check.
        var lookbackDays = Math.Max(
            Math.Max(1, providerConfig.SyncLookbackDays), providerConfig.AuditLookbackDays);

        // No LastSyncDate stamp and no SyncError transition: see IDeviceSyncService.AuditSyncAsync.
        // Any revision this turns up still lands in the raw row and is merged, so the audit
        // repairs history as a side effect of measuring it.
        var (_, today) = await MemberTodayAsync(connection);
        await PullWindowAsync(connection, accessToken, lookbackDays, today);
    }

    /// <summary>
    /// The member's local zone and today's date in it — the day every provider read means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="DateOnly"/> handed to <see cref="IDeviceApiClient"/> is a <em>civil</em>
    /// day: <c>GoogleHealthApiClient</c> filters sleep on <c>civil_end_time</c>, samples on
    /// <c>civil_time</c>, intervals on <c>civil_start_time</c> and rollups on a
    /// <c>CivilDateTime</c>. Choosing that date from the UTC clock asked for the wrong day for
    /// part of every day: east of Greenwich a night that ended at 07:00 was not "today" until the
    /// UTC date rolled (10:00 at UTC+10), and west of it the evening pulls asked for a tomorrow
    /// that had not started, leaving the real evening to the next day's repair pass.
    /// </para>
    /// <para>
    /// The zone is <see cref="MemberAnchorTimeZone"/>, the clock <c>StatisticalAlertService</c>
    /// already uses to decide which of these rows is "yesterday" — so the day ingestion writes and
    /// the day the rules read cannot drift apart. It is the caregiver's zone, not the watch's; a
    /// family split across zones still pulls on one clock, and the repair pass covers the gap as
    /// it did for everyone under UTC. Costs two reads per sync, outside the provider try-block so
    /// a database failure never parks the connection in <see cref="ConnectionStatus.SyncError"/>.
    /// </para>
    /// </remarks>
    private async Task<(TimeZoneInfo Zone, DateOnly Today)> MemberTodayAsync(DeviceConnection connection)
    {
        var zone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, connection.CardiMemberId);
        return (zone, LocalDate(_clock.GetUtcNow().UtcDateTime, zone));
    }

    /// <summary>
    /// The <c>LastSyncDate</c> a successful pull records: when it finished, unless it finished
    /// after the member's midnight, in which case the last second of the day it was for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stamp does two jobs. Scheduling reads it as "when the last pull finished" —
    /// <c>GetDueForSyncAsync</c> counts <c>SyncFrequencyMinutes</c> from it — so it stays the
    /// completion time; the pull's start would make a long repair pull due again the moment it
    /// lands. The repair gate in <see cref="SyncCardiMemberAsync"/> reads its local date as "the
    /// day the last pull was for", and a pull that starts before the member's midnight and lands
    /// after it would record the new day on completion time alone: the next pull would then skip
    /// the repair pass for the day that had just closed, leaving it to a later pass's lookback.
    /// </para>
    /// <para>
    /// Clamping serves both. It only moves a stamp that crossed midnight, and then by no more than
    /// the pull took. The last whole second rather than the last tick, because the column keeps
    /// microseconds and a sub-microsecond value can round up into the next day — the exact case
    /// this exists to prevent. A local 23:59:59 that does not exist (no zone skips it today, but
    /// the rules are data) falls back to the completion time, which is only the pre-clamp race.
    /// </para>
    /// </remarks>
    private static DateTime SuccessStamp(DateTime completedAtUtc, DateOnly pulledDay, TimeZoneInfo zone)
    {
        if (LocalDate(completedAtUtc, zone) <= pulledDay)
            return completedAtUtc;

        var lastSecond = pulledDay.ToDateTime(new TimeOnly(23, 59, 59));
        return zone.IsInvalidTime(lastSecond)
            ? completedAtUtc
            : TimeZoneInfo.ConvertTimeToUtc(lastSecond, zone);
    }

    /// <summary>
    /// A stored UTC instant's date on the member's clock. <see cref="DateTimeKind.Unspecified"/>,
    /// which is what a timestamp column reads back as, is taken as UTC — that is how it was written.
    /// </summary>
    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    public async Task<int> PullHistoryRangeAsync(
        DeviceConnection connection, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (from > to)
            throw new ArgumentOutOfRangeException(nameof(from), from, "The range must not end before it starts.");

        var providerConfig = ResolveProviderConfig(connection);
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        // Newest first, like the backfill — the days a caregiver is looking at land first, and
        // the Worker records progress as the oldest day reached, so a chunk cut short by a
        // provider failure resumes from where the stored days end. No try/catch: the caller
        // owns the retry decision, and no status transition (see IDeviceSyncService).
        var daysWithData = 0;
        for (var date = to; date >= from; date = date.AddDays(-1))
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, date);
            if (!snapshot.HasAnyData)
                continue;

            // No rhythm on a re-pull, though it is otherwise "everything the provider has for
            // these days": the ECG filter admits no upper bound, so reaching a day weeks back
            // means paging forward through every reading taken since. The counts stay null, which
            // reads as "cannot see" rather than "nothing happened" — the honest answer for a day
            // nobody looked at.
            await StoreDayAsync(connection, snapshot, DeviceRhythmDay.None, date);
            daysWithData++;

            // The granular series too — a re-pull is "everything the provider has for these
            // days", not just the daily figures. Only for a day whose daily row just landed, so
            // an hour vector never exists without its daily parent (and an empty day costs no
            // extra requests). The autonomous backfill skips this because intraday depth is
            // unverified; here the caregiver asked for it, and a day the provider serves no
            // intraday data for simply comes back empty.
            var granularDay = await _deviceApi.GetGranularDayAsync(accessToken, date);
            if (granularDay is { HasAnyData: true })
                await _granularIngestion.IngestDayAsync(connection, granularDay, ct);
        }

        return daysWithData;
    }

    /// <summary>
    /// Reads the wearable's battery from the provider's device registry and stores the last-known
    /// value on the connection. One request, on every pull including a caregiver's manual refresh:
    /// the whole point of the reading is that it is current when someone looks at it, and unlike
    /// the granular series or the history backfill it does not scale with the window.
    /// </summary>
    /// <remarks>
    /// Best-effort in the strongest sense — this method never throws. Battery is a convenience
    /// reading about hardware, and no failure to obtain it may cost the member their health data
    /// or park a working connection in <see cref="ConnectionStatus.SyncError"/>. It sits before
    /// <see cref="PullWindowAsync"/> only so a caregiver opening the device list mid-sync sees a
    /// fresh battery alongside a window still landing; the ordering carries no dependency.
    /// <para>
    /// Skipped outright when the connection never granted the settings scope, so the common case
    /// for a pre-existing wearer costs no request at all rather than a guaranteed 403 every ten
    /// minutes. The client tolerates that 403 anyway, for the window where this stored scope list
    /// and the token's real grant disagree.
    /// </para>
    /// </remarks>
    private async Task CaptureBatteryAsync(DeviceConnection connection, string accessToken)
    {
        if (!DeviceScopes.GrantsSettings(ParseScopes(connection.Scopes)))
            return;

        IReadOnlyList<PairedDeviceInfo> pairedDevices;
        try
        {
            pairedDevices = await _deviceApi.GetPairedDevicesAsync(accessToken);
        }
        catch (Exception ex) when (IsProviderApiException(ex))
        {
            return;
        }

        // The provider reports every wearable on the account, and CardiTrack's connection does not
        // name which one it is. The lowest battery among the battery-powered ones is the reading
        // that matters: a caregiver needs to know something is about to stop reporting, and
        // averaging or taking the first would hide exactly that.
        var lowest = pairedDevices
            .Where(d => d.IsBatteryPowered && (d.BatteryLevel is not null || d.BatteryStatus is not null))
            .OrderBy(d => d.BatteryLevel ?? int.MaxValue)
            .FirstOrDefault();

        if (lowest is null)
            return;

        var readAt = DateTime.UtcNow;

        // Whether the *stored* reading counted as low before this one landed. A stale reading is
        // not low for this purpose no matter what number it holds — DeviceBatteryLowRule ignores
        // it on exactly the same freshness test, so treating it as low here would report a
        // transition the rule cannot see.
        var wasLow = DeviceBattery.IsFresh(connection.BatteryUpdatedAt, readAt)
                     && DeviceBattery.IsLow(connection.BatteryLevel, connection.BatteryStatus);
        var isLow = DeviceBattery.IsLow(lowest.BatteryLevel, lowest.BatteryStatus);

        await _deviceConnections.UpdateBatteryAsync(
            connection.Id, lowest.BatteryLevel, lowest.BatteryStatus, readAt);

        connection.BatteryLevel = lowest.BatteryLevel;
        connection.BatteryStatus = lowest.BatteryStatus;
        connection.BatteryUpdatedAt = readAt;

        if (wasLow != isLow)
            await ReevaluateGapsAsync(connection.CardiMemberId);
    }

    /// <summary>
    /// Re-runs gap detection for the member, so a battery that has just crossed into (or back out
    /// of) "low" opens or closes <c>DEVICE_BATTERY_LOW</c> now rather than at the next
    /// <c>DataCompletenessWorker</c> run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cadence is the whole point. The daily 06:00 sweep is the wrong clock for a device the rule
    /// itself describes as minutes-to-hours from stopping — a warning that arrives up to 24 hours
    /// after the reading has already been overtaken by the thing it was warning about. Reconciling
    /// here puts it within a sync cycle. The push follows from
    /// <c>NotificationDispatchWorker</c>'s sweep; nothing about delivery is decided in this class.
    /// </para>
    /// <para>
    /// Only on a <em>transition</em>, never on every pull. Connections sync every ten minutes and
    /// reconciliation loads a member's whole snapshot — running it each time would re-evaluate the
    /// estate roughly 144 times a day per connection to discover nothing changed. The gap opens and
    /// closes on the crossing, so the crossing is the only moment worth spending it on.
    /// </para>
    /// <para>
    /// Best-effort, like everything else in <see cref="CaptureBatteryAsync"/>: a caller's health
    /// data must not be lost because a notification could not be reconciled. A missed call here
    /// delays the nudge to the daily run — which is exactly where it stood before this existed.
    /// </para>
    /// </remarks>
    private async Task ReevaluateGapsAsync(Guid cardiMemberId)
    {
        try
        {
            await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId);
        }
        catch (Exception)
        {
            // Deliberately swallowed — see the remarks. DataCompletenessWorker remains the backstop.
        }
    }

    /// <summary>Scopes are stored as a JSON array; a malformed value must not break a sync.</summary>
    private static List<string> ParseScopes(string? scopes) =>
        JsonUtility.TryDeserialize<List<string>>(scopes ?? "[]", out var parsed, out _) && parsed is not null
            ? parsed
            : [];

    private DeviceProviderSettings ResolveProviderConfig(DeviceConnection connection) =>
        _providers.ConfigFor(connection.DeviceType)
        ?? throw new InvalidOperationException(
            $"No provider config claims device type '{connection.DeviceType}' in its DeviceTypes.");

    /// <summary>
    /// Fetches a trailing window ending at today, storing each day and re-merging it.
    /// </summary>
    /// <remarks>
    /// The window ends at <em>today</em> so the dashboard's Key Metrics move during the day.
    /// Ending it at yesterday — which is what this did until now — meant the merged
    /// <c>ActivityLogs</c> row for today never existed, so every reader of "the latest day" was
    /// serving a completed day no matter how often the caregiver pulled to refresh.
    /// <para>
    /// Today's numbers are necessarily partial, and that is the caller's problem to know about,
    /// not a reason to withhold them: <c>DashboardService</c> suppresses the
    /// compare-against-baseline reading for a day still in progress, and baselines are calculated
    /// over completed days only. What providers finalise after midnight is instead covered by the
    /// trailing days, which reach back <paramref name="lookbackDays"/> complete days, so a day
    /// missed while the puller was down is still picked up on a later run rather than being lost
    /// for good.
    /// </para>
    /// <para>
    /// <paramref name="lookbackDays"/> of 0 fetches today alone. That is the routine case: callers
    /// pass the trailing days only on the first pull of the member's local day, because re-reading
    /// finished days every ten minutes costs a per-user quota that today's numbers have a better
    /// claim on. <paramref name="today"/> is that local day too — see <see cref="MemberTodayAsync"/>.
    /// </para>
    /// </remarks>
    private async Task PullWindowAsync(
        DeviceConnection connection, string accessToken, int lookbackDays, DateOnly today)
    {
        // Checked once for the whole window rather than per day: a connection's granted scopes do
        // not change mid-pull, and the check is over a parsed JSON list.
        //
        // Two gates, not one. ReadRhythmDayAsync below fetches the combined ECG-and-IRN day and
        // each half tolerates its own absence, so it is right to run for a connection holding
        // either scope alone -- that is what the broad GrantsRhythm is for. GetIrnProfileAsync is
        // not like that: it is a single IRN-specific request, and gating it on the broad check
        // would send it on every pull from an ECG-only connection, each one a predictable 403
        // spent and logged for good.
        var scopes = ParseScopes(connection.Scopes);
        var readsRhythm = DeviceScopes.GrantsRhythm(scopes);
        if (DeviceScopes.GrantsIrn(scopes))
            await CaptureIrnProfileAsync(connection, accessToken);

        // Oldest first, so a mid-window provider failure still leaves the earlier days stored.
        for (var offset = lookbackDays; offset >= 0; offset--)
        {
            var targetDate = today.AddDays(-offset);
            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, targetDate);
            var rhythm = readsRhythm
                ? await ReadRhythmDayAsync(accessToken, targetDate)
                : DeviceRhythmDay.None;

            await StoreDayAsync(connection, snapshot, rhythm, targetDate);
            await StoreRhythmEpisodesAsync(connection, rhythm);
        }
    }

    /// <summary>
    /// One day's rhythm events, or <see cref="DeviceRhythmDay.None"/> if the provider refused.
    /// </summary>
    /// <remarks>
    /// Never throws, for the same reason <see cref="CaptureBatteryAsync"/> does not: these two data
    /// types sit behind restricted scopes and a young verification, so a provider-side refusal is a
    /// plausible steady state. Letting one park the connection in
    /// <see cref="ConnectionStatus.SyncError"/> would cost the member every other reading of the
    /// day over a read most wearers cannot make at all. The counts stay null, which is the
    /// "cannot see" answer rather than the "nothing happened" one.
    /// </remarks>
    /// <summary>
    /// Records whether the wearer is actually enrolled in AFib screening.
    /// </summary>
    /// <remarks>
    /// Worth a request per pull because the alternative is a silence nobody can read: a wearer who
    /// never finished IRN setup raises no notifications at all, which on every screen looks exactly
    /// like a wearer whose heart is behaving. Best-effort and never throws — this catch is
    /// deliberately wider than <see cref="IsProviderApiException"/>: that predicate is
    /// provider-rejection-only by design (its own doc comment excludes infrastructure failures
    /// like network timeouts, and the outer catch in <see cref="SyncCardiMemberAsync"/> uses the
    /// same predicate to decide whether to mark <see cref="ConnectionStatus.SyncError"/>, which a
    /// transient network blip should not do). A profile we cannot read for any reason, provider
    /// rejection or a plain timeout, leaves both columns null, which is "we could not ask" rather
    /// than "they are not covered" — and must not cost the rest of this pull's health data, which
    /// this call runs ahead of. There is no database write inside this try block, so widening it
    /// cannot swallow one.
    /// <para>
    /// <see cref="CaptureBatteryAsync"/> makes the same "best-effort, never throws" promise and
    /// only catches <see cref="IsProviderApiException"/> too — the same gap, not fixed here since
    /// it is outside what this change touches.
    /// </para>
    /// </remarks>
    private async Task CaptureIrnProfileAsync(DeviceConnection connection, string accessToken)
    {
        bool? onboarded;
        bool? enrolled;
        try
        {
            (onboarded, enrolled) = await _deviceApi.GetIrnProfileAsync(accessToken);
        }
        catch (Exception ex) when (IsProviderApiException(ex) || IsTransportFailure(ex))
        {
            return;
        }

        // Nothing readable and nothing stored before: writing three nulls over three nulls is a
        // round trip to the database to change nothing.
        if (onboarded is null && enrolled is null && connection.IrnProfileUpdatedAt is null)
            return;

        var readAt = DateTime.UtcNow;
        await _deviceConnections.UpdateIrnProfileAsync(connection.Id, onboarded, enrolled, readAt);
        connection.IrnOnboarded = onboarded;
        connection.IrnEnrolled = enrolled;
        connection.IrnProfileUpdatedAt = readAt;
    }

    private async Task<DeviceRhythmDay> ReadRhythmDayAsync(string accessToken, DateOnly targetDate)
    {
        try
        {
            return await _deviceApi.GetRhythmDayAsync(accessToken, targetDate);
        }
        catch (Exception ex) when (IsProviderApiException(ex))
        {
            return DeviceRhythmDay.None;
        }
    }

    /// <summary>
    /// Stores the beat-level detail behind the day's irregular-rhythm notifications.
    /// </summary>
    /// <remarks>
    /// Written per device rather than merged across them, unlike the day counts: a window is a
    /// measurement one watch made, and two watches disagreeing about the same minutes is
    /// information rather than a conflict to resolve.
    /// <para>
    /// Best-effort <em>in fact</em>, not merely in this comment: the write runs before the sync is
    /// marked successful, so an escaping database error — a missing partition, a transient
    /// failure — would fail the whole member sync over beat-detail enrichment and re-fetch the
    /// entire window on the next pull. The episodes are evidence attached to an alert the day
    /// counts already raise, and those counts have landed by the time this runs, so a failure here
    /// costs detail and never the finding.
    /// </para>
    /// </remarks>
    private async Task StoreRhythmEpisodesAsync(DeviceConnection connection, DeviceRhythmDay rhythm)
    {
        if (rhythm.AnalysisWindows.Count == 0)
            return;

        try
        {
            await WriteRhythmEpisodesAsync(connection, rhythm);
        }
        catch (Exception ex)
        {
            // Deliberately not claiming the next pull will retry this. Only the first pull of the
            // member's local day carries the repair lookback; every later pull that day runs with
            // lookback 0, so a window from a repair day that fails to write here is not re-read and
            // its beat detail is gone for good. The day's counts survive — they were written before this —
            // so what is lost is the evidence behind a finding, not the finding. Persisting a
            // retry queue for beat detail is the follow-up this log is honest about needing.
            _logger?.LogError(
                ex,
                "Rhythm episode write failed for connection {DeviceConnectionId} on {WindowCount} "
                + "window(s); the day's counts are unaffected, but beat detail for a repair-day "
                + "window will not be re-read by a later pull.",
                connection.Id,
                rhythm.AnalysisWindows.Count);
        }
    }

    private async Task WriteRhythmEpisodesAsync(DeviceConnection connection, DeviceRhythmDay rhythm)
    {
        var ingestedAt = DateTime.UtcNow;

        foreach (var window in rhythm.AnalysisWindows)
        {
            var rr = window.Beats.Select(b => b.RrMs).ToArray();
            var (mean, min, max, rmssd) = RhythmEpisodeStatistics.Summarise(rr);

            await _unitOfWork.RhythmEpisodes.UpsertAsync(new RhythmEpisode
            {
                CardiMemberId = connection.CardiMemberId,
                WindowStartUtc = window.StartUtc,
                WindowEndUtc = window.EndUtc,
                DeviceConnectionId = connection.Id,
                NotificationStartUtc = window.NotificationStartUtc,
                Positive = window.Positive,
                BeatCount = rr.Length,
                RrMilliseconds = rr,
                OffsetMillisFromStart = window.Beats.Select(b => b.OffsetMs).ToArray(),
                MeanRrMs = mean,
                MinRrMs = min,
                MaxRrMs = max,
                RmssdMs = rmssd,
                IngestedAtUtc = ingestedAt,
            });
        }
    }

    /// <summary>
    /// Fetches and stores the granular (minute-grain) series for the routine window's days.
    /// Runs only after the whole daily window landed and was marked successful, so an hour
    /// vector never exists without its daily parent and a granular failure never un-succeeds
    /// the sync a caregiver depends on. Backfill days deliberately get no granular pass: how
    /// far back the provider serves intraday history is unverified (granular ADR open
    /// question), so history stays daily-grain until the probe answers it.
    /// </summary>
    private async Task IngestGranularWindowAsync(
        DeviceConnection connection, string accessToken, int lookbackDays, DateOnly today)
    {
        for (var offset = lookbackDays; offset >= 0; offset--)
        {
            var targetDate = today.AddDays(-offset);
            var granularDay = await _deviceApi.GetGranularDayAsync(accessToken, targetDate);
            if (granularDay is { HasAnyData: true })
                await _granularIngestion.IngestDayAsync(connection, granularDay);
        }
    }

    /// <summary>
    /// Says what is known about last night and the night before — slept, awake with the watch on,
    /// no data, or pending — now that the overnight heart rate this pull fetched has landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After the granular pass, because the awake rule reads the heart rate that pass stores; and
    /// every pull rather than once, because a night's status moves as its data arrives — pending
    /// until the morning's sync, then awake, or slept once a late session lands. Two nights, not
    /// the repair window: a night older than that has had its morning sync, and the re-merge keeps
    /// its status (<c>ActivityLogAggregationService.RecomputeAsync</c>).
    /// </para>
    /// <para>
    /// Best-effort, like the rhythm write: the day's figures landed before this runs, and a failed
    /// heart-rate read costs one pull's worth of freshness on the status, never the day. The next
    /// pull reads it again. It also must not cost the backfill that runs after it.
    /// </para>
    /// </remarks>
    private async Task ClassifyRecentNightsAsync(DeviceConnection connection, DateOnly today)
    {
        try
        {
            await _aggregation.ClassifyNightAsync(connection.CardiMemberId, today.AddDays(-1));
            await _aggregation.ClassifyNightAsync(connection.CardiMemberId, today);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(
                ex,
                "Night sleep classification failed for CardiMember {CardiMemberId}; the day's figures "
                + "are unaffected and the next pull classifies again.",
                connection.CardiMemberId);
        }
    }

    /// <summary>
    /// Extends a connection's stored history backwards, one chunk per pull, until
    /// <see cref="DeviceProviderSettings.BackfillDays"/> days back from today have been checked.
    /// </summary>
    /// <remarks>
    /// A fresh connection starts with only the routine window, which leaves the 30-day baseline
    /// unreachable for weeks even when the wearable holds months of history the provider would
    /// serve today. Fetching it in one pull is not an option either: a day's snapshot costs up to
    /// 21 requests against the 300/min per-wearer ceiling, so a 90-day one-shot (up to 1,890
    /// requests at one page per series, more when a series spans several) would rate-limit
    /// partway, fail the sync, and start over from scratch on the next pull — burning quota
    /// without ever completing. Each pull therefore takes one chunk, newest-first (the days the
    /// 30-day baseline needs soonest), and <see cref="DeviceConnection.HistoryBackfilledTo"/>
    /// advances per day so an interrupted chunk resumes where it stopped.
    /// <para>
    /// Days the provider has nothing for are checked but not stored: an all-null row would read
    /// as a "data day" to <c>BaselineCalculator</c>'s coverage gate and the dashboard's
    /// days-captured figure, letting a mostly-empty history fake its way past both. The marker
    /// still advances — checked-and-empty is a final answer, not a retry.
    /// </para>
    /// </remarks>
    private async Task BackfillHistoryAsync(
        DeviceConnection connection, string accessToken, DeviceProviderSettings providerConfig, DateOnly today)
    {
        if (providerConfig.BackfillDays <= 0)
            return;

        var horizon = today.AddDays(-providerConfig.BackfillDays);

        // Connections that predate the marker resume from the routine window's floor. That
        // re-fetches at most the trailing repair days once, which is cheaper than querying for
        // the true edge of what is already stored.
        var frontier = connection.HistoryBackfilledTo
            ?? today.AddDays(-Math.Max(1, providerConfig.SyncLookbackDays));

        var chunkFloor = frontier.AddDays(-Math.Max(1, providerConfig.BackfillChunkDays));
        for (var date = frontier.AddDays(-1); date >= horizon && date >= chunkFloor; date = date.AddDays(-1))
        {
            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, date);
            // Null rhythm for the same reason the re-pull passes none: these are old days, and the
            // ECG read can only walk backwards from now.
            if (snapshot.HasAnyData)
                await StoreDayAsync(connection, snapshot, DeviceRhythmDay.None, date);

            await _deviceConnections.UpdateHistoryBackfilledToAsync(connection.Id, date);
            connection.HistoryBackfilledTo = date;
        }
    }

    /// <summary>
    /// Stores one day's snapshot as this device's raw row and re-merges the member's day.
    /// </summary>
    private async Task StoreDayAsync(
        DeviceConnection connection,
        DeviceHealthSnapshot snapshot,
        DeviceRhythmDay rhythm,
        DateOnly targetDate)
    {
        var log = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = connection.CardiMemberId,
            DeviceConnectionId = connection.Id,
            DataSource = connection.DeviceType,
            Date = targetDate,

            // Activity
            Steps = snapshot.Steps,
            Distance = snapshot.DistanceKm,
            ActiveMinutes = snapshot.ActiveMinutes,
            SedentaryMinutes = snapshot.SedentaryMinutes,
            Floors = snapshot.Floors,
            CaloriesBurned = snapshot.CaloriesBurned,

            // Heart rate
            RestingHeartRate = snapshot.RestingHeartRate,
            AvgHeartRate = snapshot.AvgHeartRate,
            MaxHeartRate = snapshot.MaxHeartRate,
            MinHeartRate = snapshot.MinHeartRate,

            // Sleep
            SleepMinutes = snapshot.TotalSleepMinutes,
            SleepEfficiency = snapshot.SleepEfficiency,
            SleepStartTime = snapshot.SleepStartTime,
            SleepEndTime = snapshot.SleepEndTime,
            DeepSleepMinutes = snapshot.DeepSleepMinutes,
            LightSleepMinutes = snapshot.LightSleepMinutes,
            RemSleepMinutes = snapshot.RemSleepMinutes,
            AwakeMinutes = snapshot.AwakeMinutes,

            // Additional health metrics — null until a provider populates them
            SpO2Average = snapshot.SpO2Average,
            SpO2Min = snapshot.SpO2Min,
            SpO2Max = snapshot.SpO2Max,
            VO2Max = snapshot.VO2Max,
            StressScore = snapshot.StressScore,
            BreathingRate = snapshot.BreathingRate,
            Temperature = snapshot.Temperature,
            TemperatureBaseline = snapshot.TemperatureBaseline,
            TemperatureVariation = snapshot.TemperatureVariation,

            // Overnight readings
            HeartRateVariabilityMs = snapshot.HeartRateVariabilityMs,
            OvernightBreathingRate = snapshot.OvernightBreathingRate,

            // Effort and rest
            LightZoneMinutes = snapshot.LightZoneMinutes,
            ModerateZoneMinutes = snapshot.ModerateZoneMinutes,
            VigorousZoneMinutes = snapshot.VigorousZoneMinutes,
            PeakZoneMinutes = snapshot.PeakZoneMinutes,
            ModerateZoneFloorBpm = snapshot.ModerateZoneFloorBpm,
            LongestSedentaryStretchMinutes = snapshot.LongestSedentaryStretchMinutes,
            LongestSedentaryStretchStartUtc = snapshot.LongestSedentaryStretchStartUtc,

            // Null throughout for a connection without the rhythm scopes — "we cannot see this",
            // which the alert rules and every prompt treat differently from a zero.
            EcgReadings = rhythm.EcgReadings,
            EcgAtrialFibrillationReadings = rhythm.EcgAtrialFibrillationReadings,
            IrregularRhythmNotifications = rhythm.IrregularRhythmNotifications
        };

        // Save the raw row first — the merge reads every device's stored row for the day,
        // so it has to see this one.
        await _deviceActivityLogs.UpsertAsync(log);
        await _unitOfWork.SaveChangesAsync();

        await _aggregation.RecomputeAsync(connection.CardiMemberId, targetDate);
        await _unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// Returns true for exceptions that represent a provider API failure (as opposed to
    /// infrastructure failures like network timeouts). Override in provider-specific subclasses
    /// if needed, or broaden to catch a common base exception type.
    /// </summary>
    protected virtual bool IsProviderApiException(Exception ex) =>
        ex is GoogleHealthApiException;

    /// <summary>
    /// The infrastructure half <see cref="IsProviderApiException"/> deliberately excludes: a
    /// timeout or a connection failure reaching the provider at all, as opposed to the provider
    /// answering and rejecting the request. Distinct from that predicate on purpose -- most
    /// callers in this class want a transport failure to propagate (it is not the provider saying
    /// no, and the outer catch's SyncError transition should not fire for a local network blip) --
    /// so this exists for the few call sites that are optional enrichment and must swallow either
    /// category equally, the way <see cref="CaptureIrnProfileAsync"/> does.
    /// </summary>
    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;
}
