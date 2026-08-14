# CardiTrack Worker Service

## Overview

`CardiTrack.Worker` hosts the platform's **non-AI scheduled background jobs**, driven by cron expressions and the [Cronos](https://github.com/HangfireIO/Cronos) library. Although it is a background service, the project uses the **`Microsoft.NET.Sdk.Web` SDK with `Exe` output** — Cloud Run requires an HTTP listener for startup probes, so the worker binds Kestrel to the `PORT` env var (default 8080) and exposes a minimal `GET /healthz` endpoint alongside its hosted services.

The 11 workers registered today (crons from `appsettings.json`):

| Worker | Default cron (UTC) | Purpose |
|---|---|---|
| `WearableSyncWorker` | `0 */10 * * * *` (every 10 min) | Polls due device connections and syncs wearable data |
| `OrphanedOrganizationCleanupWorker` | `0 0 3 * * *` (daily 03:00) | Deletes organizations stranded by a failed onboarding |
| `BaselineCalculationWorker` | `0 30 2 * * *` (daily 02:30) | Recalculates each member's `PatternBaseline` rows — 7/14-day provisional and 30/60/90-day windows |
| `PartitionMaintenanceWorker` | `0 15 * * * *` (hourly; `RunOnStartup: true`) | Pre-creates partitions for the partitioned time-series tables and drops the ones past retention — granular 90 d, hourly rollups 13 mo, **digests 3 mo, real-time assessments 90 d, environmental readings 90 d** |
| `DeviceSyncAuditWorker` | `0 0 4 * * 0` (Sunday 04:00) | Re-fetches a small random sample over a 14-day window to measure how far back each provider revises data |
| `InactivityDetectionWorker` | `0 */15 * * * *` (every 15 min) | Device-silence failsafe — one yellow `Inactivity` alert when a member has no granular readings for >2 h in waking hours |
| `StatisticalAlertWorker` | `0 7-59/15 * * * *` (every 15 min, offset) | R1 statistical alert engine — five deterministic rules vs the established 30-day baseline |
| `DeviceAuthRecoveryWorker` | `0 3-59/15 * * * *` (every 15 min, offset) | Retries provider-refused refresh tokens on a per-connection widening backoff |
| `DataCompletenessWorker` | `0 0 6 * * *` (daily 06:00) | Reconciles data-completeness nudges per caregiver against what each account supplies |
| `NotificationDispatchWorker` | `*/30 * * * * *` (every 30 s) | The push spine's pump — claims due outbox rows, retries, escalates, expires |
| `PushCanaryWorker` | `0 */15 * * * *` (every 15 min) | Sends a real Safety push to configured test devices and screams if the previous one never acked |

OAuth token refresh is **not a separate cron job** — it happens inside the sync path (`DeviceSyncService` calls `IOAuthTokenRefreshService` before hitting the provider API), with `DeviceAuthRecoveryWorker` retrying only the connections the provider has refused. Trial expiration reminders and the general data-retention job are **planned** but not yet implemented; the partitioned tables' retention is the exception, live via `PartitionMaintenanceWorker` — it covers the granular tables **and** `DigestEntries` (12 months), `RealtimeAssessments` (90 days) and `EnvironmentalReadings` (90 days).

> **Scope note:** the AI ingestion/inference pipeline (webhook aggregation, pre-processing, MedGemma calls, severity routing, digests) is **live in dev** — Pub/Sub + dedicated Cloud Run services per [llm_design.md](../../llm_design.md) (prod gated off). The `WearableSyncWorker` polling job below is the **guaranteed fallback** and runs in every environment; the registered webhook path triggers the same sync sooner, never a duplicate (see [release_matrix.md](../../release_matrix.md)).

## Technology Stack

- **.NET 10**: Core framework (`Microsoft.NET.Sdk.Web`, `OutputType=Exe`)
- **Cronos 0.13.0**: Cron expression parsing (HangfireIO)
- **BackgroundService**: Built-in .NET hosted service base class
- **Keyed DI** (.NET 10): Per-provider sync service dispatch
- **Entity Framework Core (Npgsql)**: PostgreSQL data access; `Npgsql.EnableLegacyTimestampBehavior` is disabled so all `timestamptz` values surface as UTC
- **Serilog / `ILogger`**: Structured logging (console + APM shipping via `CardiTrack.Observability`)

## Project Structure

```
src/Worker/CardiTrack.Worker/
├── Workers/
│   ├── WearableSyncWorker.cs                # Polls + syncs due device connections
│   ├── OrphanedOrganizationCleanupWorker.cs # Sweeps orgs with no user/CardiMember
│   ├── BaselineCalculationWorker.cs         # Recalculates PatternBaseline rows daily
│   ├── PartitionMaintenanceWorker.cs        # Creates/drops time-series partitions (retention)
│   ├── DeviceSyncAuditWorker.cs             # Wide-window re-fetch over a sample, to measure revisions
│   ├── InactivityDetectionWorker.cs         # Device-silence failsafe (yellow Inactivity alert)
│   ├── StatisticalAlertWorker.cs            # R1 statistical alert engine (five rules)
│   ├── DeviceAuthRecoveryWorker.cs          # Retries provider-refused refresh tokens (backoff)
│   ├── DataCompletenessWorker.cs            # Reconciles data-completeness nudges per caregiver
│   ├── NotificationDispatchWorker.cs        # Push outbox pump: claim, retry, escalate, expire
│   └── PushCanaryWorker.cs                  # End-to-end push liveness canary (incl. PushCanaryOptions)
├── CronBackgroundService.cs       # Abstract base — parses cron, loops on schedule (+ RunOnStartup)
├── WorkerOptions.cs               # { CronExpression, RunOnStartup } options record
├── DeviceSyncAuditOptions.cs      # { SampleSize } for the audit worker
├── InactivityDetectionOptions.cs  # Silence threshold + waking-hours window
├── PartitionMaintenanceOptions.cs # DaysAhead + the five per-table retention values
├── WorkerServiceExtensions.cs     # Generic AddWorker<T> registration helper
├── Program.cs                     # Host setup, DI registration, /healthz endpoint
├── Dockerfile                     # Chiseled aspnet runtime image
├── Properties/launchSettings.json
├── appsettings.json
└── CardiTrack.Worker.csproj       # SDK: Microsoft.NET.Sdk.Web, Cronos 0.13.0
```

## Core Components

### CronBackgroundService

Abstract base class that drives any scheduled job via a cron expression.

```csharp
public abstract class CronBackgroundService : BackgroundService
{
    private readonly CronExpression _cron;
    private readonly TimeZoneInfo _timeZone;

    protected CronBackgroundService(string cronExpression, TimeZoneInfo? timeZone = null)
    {
        _cron = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = _cron.GetNextOccurrence(now, _timeZone);
            if (next is null) break;

            var delay = next.Value - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
                await ExecuteJobAsync(stoppingToken);
        }
    }

    protected abstract Task ExecuteJobAsync(CancellationToken stoppingToken);
}
```

### WearableSyncWorker

Reads its cron schedule from the named `WorkerOptions` (see [Configuration](#configuration)), creates a DI scope per run, and dispatches to the keyed `IDeviceSyncService` for each due device connection.

**Due** means the connection's own `SyncFrequencyMinutes` has elapsed since its `LastSyncDate` — the interval is per connection, not a fixed threshold, so the cron schedule sets only how often the worker *looks*. Every due connection syncs, including several belonging to the same CardiMember.

`GetDueForSyncAsync` also excludes connections whose CardiMember has been removed or has monitoring paused. That filter lives in the query rather than the worker so every caller inherits it — pausing monitoring has to actually stop collection, not merely change what the app displays.

### Two-tier health data

Wearable data lands in two tables:

| Table | Grain | Written by |
|-------|-------|------------|
| `DeviceActivityLogs` | one row per **device** per day — unique on `(DeviceConnectionId, Date)` | `DeviceSyncService`, straight from the provider |
| `ActivityLogs` | one row per **CardiMember** per day — unique on `(CardiMemberId, Date)` | `ActivityLogAggregationService`, derived from the raw rows |

Every reader (`DashboardService`, `HealthInsightService`, `ReportGenerationService`, the chat endpoint) consumes `ActivityLogs` only, so a member wearing two devices still presents as one clean daily series.

The merge (`ActivityLogMerge`) resolves **each metric independently**: the first device, in priority order, that reported a non-null value wins. Values are **never summed** — a watch and a ring worn by the same person both count the same steps, so adding them would double-count. Coalescing instead lets devices fill each other's gaps, which is the real benefit of wearing more than one: the ring supplies sleep and SpO2, the watch supplies steps and heart rate.

Priority is `IsPrimary` desc → `ConnectedDate` asc → `Id`, the same ordering everywhere. A raw row whose connection has since been removed is kept and simply sorted last, so deleting a device never silently drops history.

Because the merge always rebuilds from the full raw set for that member-day, it is idempotent and order-independent — re-running it, or running it after any device's row changes, converges on the same result. A provider that later revises a day is picked up on the next sync.

> **Providers must report a missing metric as `null`, never `0`.** The merge coalesces on the first non-null value, so a placeholder `0` from a higher-priority device would beat another device's genuine reading.

### What `UpdatedDate` means on a raw row

`DeviceActivityLogs.UpdatedDate` records **when the provider's numbers last changed**, not when the row was last written. `UpsertAsync` assigns the fields on the already-tracked entity and stops there — it deliberately does not call `_dbSet.Update()`, which would mark the whole entity `Modified` and make `CardiTrackDbContext.UpdateTimestamps` stamp the column on every sync.

The distinction matters because the trailing window means most upserts legitimately carry values the row already holds. If those stamped `UpdatedDate`, the column would only ever describe our own polling schedule, and two things that read it would be measuring the wrong thing:

| Derived from `UpdatedDate` | Question it answers |
|---|---|
| **Settle latency** | How long after a day ends does this provider stop revising it? — sets how soon a pull is worth making |
| **Revision tail** | How far back does it still amend? — sets how wide the trailing window has to be |
| **Poll yield** | What fraction of pulls find anything new? — once webhook ingestion lands, this doubles as the webhook miss rate |

A side effect worth having: an unchanged day no longer issues an `UPDATE` at all, so a routine sync of a settled window is now read-only.

```csharp
public class WearableSyncWorker : CronBackgroundService
{
    public WearableSyncWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<WearableSyncWorker> logger)
        : base(options.Get(nameof(WearableSyncWorker)).CronExpression)
    { ... }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var deviceConnections = scope.ServiceProvider
            .GetRequiredService<IDeviceConnectionRepository>();

        // Due per each connection's own SyncFrequencyMinutes
        var connections = await deviceConnections.GetDueForSyncAsync();

        foreach (var connection in connections)
        {
            // Keyed by DeviceType — returns null for unregistered providers
            var syncService = scope.ServiceProvider
                .GetKeyedService<IDeviceSyncService>(connection.DeviceType);

            if (syncService is null)
            {
                _logger.LogWarning("No sync service for {DeviceType}. Skipping.", connection.DeviceType);
                continue;
            }

            await syncService.SyncCardiMemberAsync(connection, SyncScope.WorkerCadence);
        }
    }
}
```

Each sync goes through `DeviceSyncService`, which first refreshes the connection's OAuth token via `OAuthTokenRefreshService` when needed — token refresh is part of the sync path, not a standalone job.

It then fetches **today** — so the dashboard's Key Metrics move during the day rather than sitting on a completed day until midnight — and, on the first pull of each UTC day, a **trailing window** reaching back `DeviceProviders:<provider>:SyncLookbackDays` (default **3**) complete days, which is what covers providers finalising a day only after midnight. Splitting the two is what makes a 10-minute cadence affordable: a day's snapshot costs 13 Google Health requests against a ceiling of 300 per minute **per wearer**, so paying for four days on every pull would spend the budget re-reading finished days. Days are fetched oldest first; each is written to `DeviceActivityLogs` and saved, then that member-day is re-merged into `ActivityLogs`. The raw row is saved before the merge runs because the merge reads every device's *stored* row for the day. A provider failure part-way through still leaves the earlier days stored; `LastSyncDate` is stamped only once the whole window lands, which keeps a partially-synced connection due for retry instead of silently leaving a hole.

After a successful routine window, the Worker's pulls also **backfill history**: `DeviceConnection.HistoryBackfilledTo` walks backwards from the routine window towards `DeviceProviders:<provider>:BackfillDays` (default **90**) days ago, `BackfillChunkDays` (default **7**) days per pull, newest first. A freshly connected wearable's existing history therefore reaches the 30-day baseline within a couple of hours instead of the baseline waiting a month for new days. The chunking is what keeps this inside the per-wearer request ceiling — a 90-day one-shot would rate-limit partway and start over on the next pull. The frontier advances per day fetched, so an interrupted chunk resumes rather than refetching; days the provider has nothing for are checked but not stored, because an all-null row would count as a "data day" to the baseline coverage gate. Only the Worker opts into this (`SyncScope.WorkerCadence`) — the API's manual sync shares `SyncCardiMemberAsync` at `SyncScope.Routine`, and a caregiver waiting on a refresh must not pay for a chunk of last month.

After the routine window has landed and been marked successful, the worker cadence also fetches each window day's **granular series** — heart rate and SpO2 as timestamped samples, steps and active-zone minutes as intervals, 4 extra requests per day (worker-cadence day cost: **17**). Granular runs outside the success envelope on purpose: it is enrichment, and a transient failure in it must not un-succeed the daily data a caregiver depends on. `GranularDayBucketer` turns them into per-device hour vectors (additive metrics sum within a minute; level metrics take the latest reading), and `GranularIngestionService` stores them and recomputes the member's hourly rollups from the **merged** window — the daily pipeline's raw-then-derived shape, at hour grain. Backfill days stay daily-grain until the intraday-history probe answers how far back the provider serves minute data.

### OrphanedOrganizationCleanupWorker

Safety net behind the API's atomic `POST /api/Onboarding/setup` endpoint. The legacy two-call onboarding flow (`POST organization` then `POST user`) can strand an organization if the client dies between calls; this worker sweeps them up.

- Runs daily at 03:00 UTC (`0 0 3 * * *` by default).
- Calls `IOrganizationRepository.DeleteOrphanedAsync(MinAge)` with **`MinAge = 24 hours`** — far longer than any onboarding gap, so an in-flight signup is never swept.
- An organization is *orphaned* when it has **no users and no CardiMembers**; its trial subscription is removed with it via the `Subscription → Organization` FK cascade.
- When anything is removed it logs at **Warning**, deliberately: orphans mean some client bypassed the atomic setup endpoint and failed mid-onboarding — worth investigating, not just cleaning. A no-op run logs at Information.

### BaselineCalculationWorker

Turns accumulated `ActivityLog` history into `PatternBaseline` rows — the statistical picture of "a normal day" that `DashboardService` colours today's metrics against, and the thing that ends a member's *"getting to know you"* phase (`DashboardService` treats a member with no baseline at all as still learning; a 7/14-day window serves as a **provisional** baseline until the 30-day one exists, and provisional baselines never fire alerts).

- Runs daily at 02:30 UTC (`0 30 2 * * *` by default). The coverage gate in `BaselineCalculator` decides when a member has been observed for long enough; the daily cadence means the first baseline lands the morning after eligibility rather than up to a week later.
- Selects **active members with at least one activity log in the last 90 days** (`ICardiMemberRepository.GetActiveIdsWithActivitySinceAsync`), so dormant records are not rescanned on every run.
- Windows to the **last complete day**, not today. Ingestion stores the day in progress so the dashboard can show live numbers, and a part-finished day averaged in would drag every member's "normal" down by however far through the day the job happened to run.
- Fetches each member's logs **once** for the longest period and calculates every supported window (7/14 provisional, 30/60/90) from that one read.
- Uses **one DI scope per member**: the read tracks up to 90 rows each, which would accumulate across the whole run on a shared `DbContext`, and a member that fails takes nothing else down with it.
- **Appends** rather than replacing, so a shift in a member's own normal stays visible in history. Unlike the partitioned tables, **baselines have no retention today** — pruning falls under the planned retention job (see [dpia.md](../../compliance/dpia.md) §6.3).

The arithmetic lives in `BaselineCalculator` (`CardiTrack.Application/Services`) — pure and stateless, so it is unit-tested without a database or a clock. Its rules:

| Rule | Behaviour |
|---|---|
| Coverage gate | No baseline for a window unless **80% of it** has data (24 of 30 days; 6 of 7 for the shortest provisional window). Below that the window is skipped rather than scored against an average of almost nothing. |
| Per-metric floor | Each metric needs **7 samples** of its own (scaled down to the window's coverage bar for windows shorter than 9 days); ingestion populates metrics unevenly, so a thin metric is left null instead of averaged. |
| Spread | **Sample** standard deviation (n−1) — the dashboard turns σ into the member's normal range, so the population form would narrow that band on every member. |
| Bedtime / wake time | **Circular** mean over the 24-hour clock; an arithmetic mean of 23:40 and 00:20 is midday. Reported in **UTC** — `CardiMember` carries no timezone. |
| Weekday profile | Monday-first JSON array of average steps. A weekday with fewer than 2 samples is `null`, not `0` — "no data for Sundays" must not read as "this member does not move on Sundays". |

### PartitionMaintenanceWorker

Keeps the partitioned time-series tables (`GranularMetricHours`, `MetricRollupsHourly`, `DigestEntries`, `RealtimeAssessments`, `EnvironmentalReadings` — see the [granular-storage ADR](../../technical/granular_timeseries_storage.md)) alive: PostgreSQL neither creates range partitions on demand nor expires rows, so this job pre-creates partitions ahead of the data and drops the ones wholly past retention.

- Runs **hourly** (`0 15 * * * *`) and additionally **once at startup** — `CronBackgroundService` now supports a `RunOnStartup` mode (`WorkerOptions.RunOnStartup`, off by default) and this worker opts in (`RunOnStartup: true` in appsettings), so a fresh deploy has its partitions before the first insert rather than waiting for the next hourly tick. Creation is idempotent (`IF NOT EXISTS`) and near-free.
- Pre-creates from **yesterday** through `DaysAhead` (default **7**) days out — a sync straddling UTC midnight can still write into the day that just ended, and a week of headroom survives a multi-day worker outage.
- Retention is a **partition drop** — instant, no dead tuples to vacuum: granular hours after `GranularRetentionDays` (default **90**), hourly rollups after `RollupRetentionMonths` (default **13**), digests after `DigestRetentionMonths` (default **3**), real-time assessments after `RealtimeRetentionDays` (default **90**), environmental readings after `EnvironmentalRetentionDays` (default **90**). A partition is dropped only when its whole range is past the cutoff.
- **Never drops what it did not name**: the drop path parses each child's name against the worker's own naming scheme, so a manually attached partition is left alone regardless of age.
- Drops log at **Warning** — destroying health data past retention is the one thing this job does that an audit should be able to reconstruct.

### InactivityDetectionWorker

The device-silence failsafe (llm_design's `InactivityDetector` — placed here and not in the AI pipeline because it makes no AI call, and non-AI background jobs are Worker-exclusive per CLAUDE.md). Every generated artifact deliberately refuses to speak from silence — the digest skips, the assessor skips — so a dead watch battery would otherwise produce *nothing*; this worker turns that nothing into exactly one yellow `Inactivity` alert.

- Runs **every 15 minutes** (`0 */15 * * * *`).
- **Silence means no granular readings**, deliberately not "no successful sync" — a sync that completes and returns no new minutes is precisely the dead-battery / watch-on-the-nightstand case this alert exists to catch.
- Only counts during **waking hours on the member's anchor clock** (07:00–22:00 local via the shared `MemberAnchorTimeZone` resolution, same anchor as the digest). The whole silent window must fit inside waking hours, so alerting effectively starts at `wakingStart + threshold` (09:00 on the defaults) — a watch still on its charger never trips the first alert of the day.
- Candidates are members with data in the last two days (the same filter as digest/assessment): longer-silent members have aged out *and* already carry their standing alert.
- **Cooldown**: one unresolved `Inactivity` alert per member; resolving it re-arms the check. Config (`Workers:InactivityDetectionWorker`): `SilenceThresholdMinutes` (default **120**), `WakingStartHour` (**7**), `WakingEndHour` (**22**); invalid values skip the run loudly rather than misfire.

### StatisticalAlertWorker

The R1 statistical alert engine: five deterministic rules (`docs/execution/backend/api/alerts.md` taxonomy — activity decline, irregular sleep, elevated resting HR, no morning activity, long-term trend) evaluated against each member's **established 30-day baseline** — fetching only that baseline is how "provisional baselines never alert" is enforced. Pure rules in `StatisticalAlertRules` (Application, I/O-free, boundary-tested); orchestration in `StatisticalAlertService`.

- Runs **every 15 minutes**, offset from the inactivity worker (`0 7-59/15 * * * *`) — the cadence exists for the one intraday rule (`no_morning_activity`, red: measured-zero steps past typical wake + 2 h while the device reports); daily-grain rules are held to once per local day by the same-day dedup.
- Thresholds are the hard-coded **medium** sensitivity profile (>30% deviation; HR margin max(2σ, 5 bpm); trend ≥5%/week × 4 weeks). Low/high profiles wait on the unbuilt `AlertPreferences` table.
- **Null-vs-zero discipline holds**: a null reading (not measured) never fires anything — most critically in `no_morning_activity`, where an HR-only device's absent steps field must never page a family red.
- **Cooldowns follow the family's remedy** (`AlertRuleMarkers`): rule-scoped everywhere except `HeartRate`, which is type-scoped across this engine and the AI assessor.

### DeviceSyncAuditWorker

Measures something `WearableSyncWorker` structurally cannot see. A routine sync only ever looks inside its own trailing window, so with `SyncLookbackDays: 3` a provider that amends day 5 is not merely unmeasured — it is *unmeasurable*, and any picture of "how far back data changes" built from routine syncs alone would be an artefact of our own schedule rather than a fact about the provider.

- Runs weekly, Sunday 04:00 UTC (`0 0 4 * * 0`), after the baseline and cleanup jobs.
- Re-fetches a **random sample** of `SampleSize` connections (default **25**) over `AuditLookbackDays` (default **14** — the widest range the Google Health API accepts for heart-rate, active-zone-minutes and calorie roll-ups). Randomised because any stable ordering would audit the same connections forever and characterise one corner of the population.
- Eligibility is identical to the routine query minus due-ness: `GetRandomSyncableSampleAsync` excludes removed and monitoring-paused members, because **an audit still collects a member's health data** and pausing has to stop every collection path, not just the scheduled one.
- Goes through `IDeviceSyncService.AuditSyncAsync`, which shares the pull-and-merge core with the routine sync but **stamps nothing**: no `LastSyncDate` (that would push the connection's next routine pull out by a full interval, so a job that only measures would quietly change what gets collected) and no `SyncError` transition (a historical day failing says nothing about whether the connection works now).
- It still stores and merges whatever it finds, so a provider's late correction to a member's history is **repaired as a side effect of measuring it**.
- Failures log at **Warning**, not Error: an audit failure costs measurement precision, not data.

### DeviceAuthRecoveryWorker

Retries the refresh token of device connections the provider has **refused**, so a connection that can come back does so on its own (`DeviceAuthRecoveryService`) rather than waiting for a caregiver to notice and reconnect.

- Runs **every 15 minutes**, offset from the other quarter-hour jobs (`0 3-59/15 * * * *`). Fifteen minutes is the *pass* cadence, not the retry cadence — each connection carries its own **widening backoff** (migration `AddDeviceAuthRecoveryBackoff`), so a pass mostly finds nothing due.
- Worker-hosted per CLAUDE.md: no AI call is involved, and DB polling belongs here.
- Logs at Information only when it actually returns connections to service.

### DataCompletenessWorker

Detects the gaps between what CardiTrack needs and what each account has supplied, and reconciles them against what the caregiver has already been told — the engine behind the in-app data-completeness nudges ([notification_engine.md](../../technical/notification_engine.md)).

- Runs **daily at 06:00 UTC** (`0 0 6 * * *`) — after `BaselineCalculationWorker`, so anything it says about learning progress reflects that morning's numbers rather than yesterday's.
- Takes a **non-blocking Postgres advisory lock** for the run: a second Cloud Run instance that cannot take it skips the run entirely instead of evaluating the whole estate again for one useful result. Reconciliation itself is idempotent by fingerprint, so a crashed run simply repeats.
- Walks organizations in batches of 50 (cursor-paged), one DI scope — and so one `DbContext` — per organization; one bad organization is logged and skipped, never costing the estate its morning run.
- Reconciliation is **per caregiver**: each user's stored notifications are diffed against their own contexts (`NudgeReconciler`), so one user's snooze cannot suppress another's notification.
- Every run writes a `NotificationRunLog` row (organizations scanned, created/resolved/suppressed counts, duration, error) — best-effort, a failed log write is a Warning, not a failed run.

### NotificationDispatchWorker

The push spine's pump ([notification_engine.md](../../technical/notification_engine.md) §6.2, §6.3, §13): claims due `NotificationDeliveries` outbox rows, retries them, runs the escalation ladder, and expires past-TTL rows.

- Runs **every 30 seconds** (`*/30 * * * * *`). Deliberately **no advisory lock** — unlike `DataCompletenessWorker` — because `NotificationDeliveryRepository.ClaimDueAsync`'s `FOR UPDATE SKIP LOCKED` claim (plus its claim-lease `NextAttemptAt` advance) already lets multiple instances divide the outbox in parallel safely.
- Each tick runs **four isolated phases**, each in its own scope, each catching its own failures: (1) claim + retry due rows (batches of 100, one scope per row); (2) the **escalation sweep** — Safety-category and Red health deliveries that stay unacknowledged are re-pushed, then fanned out to every other caregiver with `ReceiveAlerts`, then marked `UNDELIVERED_CRITICAL` with a `LogCritical`; (3) expiring past-TTL rows to `DeadLettered`; (4) disabling push tokens with **no liveness signal (ack or foreground heartbeat) in 7 days**.
- The phase isolation is load-bearing, not defensive decoration: a misconfigured `FirebaseApp` once made resolving `IDispatchService` throw on every sweep, and because this host doesn't override `BackgroundServiceExceptionBehavior`, every tick took the whole Worker down — 224 host restarts in two hours (incident 2026-08-12), during which **nothing else the Worker owns ran either**. A push outage should cost pushes, nothing more.

### PushCanaryWorker

Sends a real Safety-category push to a fleet of configured test devices every 15 minutes and checks whether the **previous** run's canary got acked ([notification_engine.md](../../technical/notification_engine.md) §13). Provider outages, expired credentials and a broken APNs certificate are otherwise silent failures — the kind discovered from a support ticket during an actual emergency.

- Runs **every 15 minutes** (`0 */15 * * * *`); the 15-minute ack window matches the escalation ladder's own SLO.
- Deliberately routed through the **normal outbox** (`IDispatchService.EnqueueAsync`) rather than a bespoke send path — the canary is only meaningful if it exercises the exact same planning, send, and ack code every real Safety alert goes through.
- The fleet is **configured, not auto-discovered** (`Workers:PushCanaryWorker:CanaryUserIds`); an empty list skips quietly rather than paging on a fleet nobody set up. A previous canary not `Delivered` by the next run logs at **Critical** and increments the `UndeliveredCritical` telemetry counter with a `canary` reason tag.

### Multi-Provider Dispatch

Providers register keyed services per **HealthApi engine** via extension methods (shared with the API in `CardiTrack.Infrastructure/Extensions/DeviceProviderServiceExtensions.cs`); which hardware brands ride an engine is configuration (the block's `DeviceTypes` list), not code:

```csharp
// Program.cs
builder.Services.AddGoogleHealthProvider();

// AddGoogleHealthProvider registers the Google Health API engine — HTTP client, keyed
// IDeviceApiClient (GoogleHealthApiClient) and keyed IDeviceSyncService, keyed by
// HealthApi.GoogleHealth — serving every DeviceType in the block's list
// (Fitbit AND GooglePixelWatch today).

// To add a new API later: create an equivalent AddGarminConnectProvider()
```

Unknown device types produce a `LogWarning` and are skipped — no crash. `AddGoogleHealthProvider` also enforces the positional-index contract: **`DeviceProviders[0]` must be the GoogleHealth provider** (deployment injects its secrets as `DeviceProviders__0__*`), and startup throws if the list is reordered, if a block's `DeviceTypes` is empty or names an unknown type, or if two blocks claim the same device type.

### Adding a new worker

`WorkerServiceExtensions.AddWorker<T>` is the generic registration pattern — one line per job:

```csharp
// WorkerServiceExtensions.cs
public static IServiceCollection AddWorker<T>(
    this IServiceCollection services, IConfiguration configuration, string name)
    where T : BackgroundService
{
    services.Configure<WorkerOptions>(name, configuration.GetSection($"Workers:{name}"));
    services.AddHostedService<T>();
    return services;
}

// Program.cs — one line per job, all 11:
builder.Services.AddWorker<WearableSyncWorker>(configuration, nameof(WearableSyncWorker));
builder.Services.AddWorker<OrphanedOrganizationCleanupWorker>(configuration, nameof(OrphanedOrganizationCleanupWorker));
builder.Services.AddWorker<BaselineCalculationWorker>(configuration, nameof(BaselineCalculationWorker));
builder.Services.AddWorker<DeviceSyncAuditWorker>(configuration, nameof(DeviceSyncAuditWorker));
builder.Services.AddWorker<PartitionMaintenanceWorker>(configuration, nameof(PartitionMaintenanceWorker));
builder.Services.AddWorker<InactivityDetectionWorker>(configuration, nameof(InactivityDetectionWorker));
builder.Services.AddWorker<StatisticalAlertWorker>(configuration, nameof(StatisticalAlertWorker));
builder.Services.AddWorker<DeviceAuthRecoveryWorker>(configuration, nameof(DeviceAuthRecoveryWorker));
builder.Services.AddWorker<DataCompletenessWorker>(configuration, nameof(DataCompletenessWorker));
builder.Services.AddWorker<NotificationDispatchWorker>(configuration, nameof(NotificationDispatchWorker));
builder.Services.AddWorker<PushCanaryWorker>(configuration, nameof(PushCanaryWorker));
```

To add a job: derive from `CronBackgroundService`, take `IOptionsMonitor<WorkerOptions>` in the constructor and pass `options.Get(nameof(YourWorker)).CronExpression` to the base, then call `AddWorker<YourWorker>(configuration, nameof(YourWorker))` and add a `Workers:YourWorker:CronExpression` entry to config. Without a config entry the `WorkerOptions` default (`"0 * * * * *"` — every minute) applies.

## Configuration

### appsettings.json

Cron schedules bind per worker class name under the `Workers` section, consumed through named `IOptionsMonitor<WorkerOptions>`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": ""
  },
  "Encryption": {
    "Key": ""
  },
  "DeviceProviders": [
    {
      "Provider": "GoogleHealth",
      "DeviceTypes": ["Fitbit", "GooglePixelWatch"],
      "ClientId": "",
      "ClientSecret": "",
      "TokenUrl": "https://oauth2.googleapis.com/token",
      "ApiBaseUrl": "https://health.googleapis.com",
      "TokenLifetimeHours": 1,
      "SyncLookbackDays": 3,
      "BackfillDays": 90,
      "BackfillChunkDays": 7
    }
  ],
  "Workers": {
    "WearableSyncWorker": {
      "CronExpression": "0 */10 * * * *"
    },
    "OrphanedOrganizationCleanupWorker": {
      "CronExpression": "0 0 3 * * *"
    },
    "BaselineCalculationWorker": {
      "CronExpression": "0 30 2 * * *"
    },
    "PartitionMaintenanceWorker": {
      "CronExpression": "0 15 * * * *",
      "RunOnStartup": true,
      "DaysAhead": 14,
      "GranularRetentionDays": 90,
      "RollupRetentionMonths": 13,
      "DigestRetentionMonths": 3,
      "RealtimeRetentionDays": 90
    },
    "DeviceSyncAuditWorker": {
      "CronExpression": "0 0 4 * * 0",
      "SampleSize": 25
    },
    "InactivityDetectionWorker": {
      "CronExpression": "0 */15 * * * *",
      "SilenceThresholdMinutes": 120,
      "WakingStartHour": 7,
      "WakingEndHour": 22
    },
    "StatisticalAlertWorker": {
      "CronExpression": "0 7-59/15 * * * *"
    },
    "DeviceAuthRecoveryWorker": {
      "CronExpression": "0 3-59/15 * * * *"
    },
    "DataCompletenessWorker": {
      "CronExpression": "0 0 6 * * *"
    },
    "NotificationDispatchWorker": {
      "CronExpression": "*/30 * * * * *"
    },
    "PushCanaryWorker": {
      "CronExpression": "0 */15 * * * *",
      "CanaryUserIds": []
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning",
      "Override": { "Microsoft": "Warning", "Microsoft.EntityFrameworkCore": "Warning" }
    }
  },
  "Apm": {
    "Engine": "",
    "Data": { "IngestUrl": "", "IngestToken": "" },
    "TracesSampleRatio": 1.0
  }
}
```

`Encryption:Key` ships empty and must be supplied at runtime — a base64-encoded 256-bit key, matching the API's (both encrypt and decrypt the same stored OAuth tokens). `docker compose` sets it for you; running standalone, use `openssl rand -base64 32` into `Encryption__Key` or user secrets. The Worker validates the key while building the host and exits if it is missing or malformed, rather than failing every token-refresh run.

### Per-device-type pull parameters

Cadence belongs to the **device type**, not to any one connection: providers differ in how quickly they finalise a day and how hard they rate-limit. These are set per environment in `infrastructure/environments/*.tfvars` under `device_pull_params`, and reach the app as `DeviceProviders__<i>__*` env vars — the same positional binding already used for provider secrets, which is why element 0 must stay the GoogleHealth provider.

| Parameter | Default | Purpose |
|---|---|---|
| `sync_lookback_days` | 3 | Trailing window each routine sync re-fetches |
| `audit_lookback_days` | 14 | Window `DeviceSyncAuditWorker` uses; must be ≥ `sync_lookback_days` |
| `min_pull_interval_minutes` | 30 (**both environments deploy 10**) | Floor on a connection's interval — derived from the provider's rate limit; dev and prod tfvars both set 10 to match the 10-minute sync cadence |
| `max_pull_interval_minutes` | 1440 | Ceiling, so dormancy backoff cannot park a connection indefinitely |
| `max_requests_per_second` | 0 | Provider-wide ceiling for sizing the pull queue; 0 leaves it unset |
| `dormancy_threshold_pulls` | 0 | Empty pulls in a row before backoff starts; **0 disables backoff** |
| `dormancy_backoff_factor` | 2.0 | Multiplier per empty pull past the threshold |

> **The bounds are the guard, and widening them is deliberately a deploy.** Cadence calibration may move a connection's interval anywhere within `[min, max]` but never outside it. A miscomputed cadence in a cardiac-monitoring product does not cost throughput — it silently delays alerts — so the range that constrains it lives in version-controlled infrastructure rather than in a table the calculator can rewrite.

Both `AddGoogleHealthProvider`'s `PostConfigure` and the Terraform variable validate the same rules (positive floor, floor ≤ ceiling, backoff factor > 1 when enabled, audit window ≥ sync window). Duplicated on purpose: the plan fails before a bad revision deploys, and the host fails fast if one is set some other way.

### Cron Format

The worker uses 6-field cron with seconds (Cronos `IncludeSeconds`):

| Expression            | Meaning                   |
|-----------------------|---------------------------|
| `0 */10 * * * *`      | Every 10 minutes          |
| `0 0 * * * *`         | Every hour                |
| `0 0 3 * * *`         | Daily at 3 AM UTC         |
| `0 0 2 * * MON`       | Every Monday at 2 AM UTC  |

### Production Secrets

Deployed configuration comes from env vars on the Cloud Run service; sensitive values are **GCP Secret Manager-backed** (`worker_secret_env_vars` in `infrastructure/main.tf`) — never in `appsettings.json`:

```
ConnectionStrings__DefaultConnection = carditrack-<env>-db-connection-string
Auth0__Domain / __Audience / __ClientId / __ClientSecret = carditrack-<env>-auth0-*
Encryption__Key                      = carditrack-<env>-encryption-key
Health__Token                        = carditrack-<env>-health-token
DeviceProviders__0__ClientId         = carditrack-<env>-devices-fitbit-client-id
DeviceProviders__0__ClientSecret     = carditrack-<env>-devices-fitbit-client-secret
Apm__Data                            = carditrack-<env>-apm-data
```

Plaintext env vars: `ASPNETCORE_ENVIRONMENT`, `GCP_PROJECT_ID`, `Apm__Engine`, `Apm__MetricsEnabled`, `Apm__TracesSampleRatio`, `Serilog__MinimumLevel__Default`, plus the per-device-type pull parameters below. The last two come from the per-service `traces_sample_ratio` / `log_minimum_level` tfvars (`worker` attribute) — traces `1.0` everywhere; the worker's log level is **`Warning` in prod and `Information` in dev** (dev sets it deliberately).

> **Consequence of the prod `Warning` baseline:** in prod the per-run `LogInformation` lines below (`triggered`, `complete. Success: n, Failed: n`) are **not emitted** — a healthy run leaves no trace, and only per-member failures (`LogWarning`/`LogError`) surface. Cron is internal to the service (`CronBackgroundService`), so there is no per-run request log to fall back on. **Dev already runs the worker at `Information`**, so the run summaries are emitted there; to get them in prod, set `log_minimum_level = { worker = "Information" }` in its tfvars.

> **Provider note:** the `GoogleHealth` provider block authenticates against **Google OAuth** and pulls data from the **Google Health API** (`health.googleapis.com`) for every device type it serves (Fitbit, Google Pixel Watch) — the legacy Fitbit Web API is decommissioned September 2026. Google access tokens are short-lived (~1 hour), hence `TokenLifetimeHours: 1`. `GoogleHealthApiClient` reads each data type by the method that type supports: `dataPoints:dailyRollUp` for the Interval and Sample metrics (including `sedentary-period`, whose `durationSum` is a protobuf `Duration` — seconds with a literal `s` suffix, `"28800s"` — converted to minutes), and `dataPoints` list for sleep sessions, for the Daily records `daily-resting-heart-rate`, `daily-vo2-max`, `daily-respiratory-rate` and `daily-sleep-temperature-derivations` (which have no rollup), and for the `oxygen-saturation` sample series. SpO2 is listed rather than rolled up because the rollup union carries no `oxygenSaturation` member at all: average, minimum and maximum are derived from the samples so all three describe one series, and the `daily-oxygen-saturation` summary is used only as a fallback for the average — its `lowerBoundPercentage`/`upperBoundPercentage` describe the day's distribution, not the lowest and highest readings, so they are never stored as min/max. Response field names, wire formats and enum members are checked against the v4 discovery document; whether each is actually populated for a given wearer's device is still pending live-sandbox verification. Note that a metric absent from a day's response is stored as **null, not 0** — an unworn or unsynced device is not a still one, and the merge and baseline both read a 0 as a real measurement.
>
> Some metrics are allowed to fail without failing the sync: a `400`/`404` on `daily-resting-heart-rate`, or on any of the optional metrics (`oxygen-saturation`, `daily-vo2-max`, `daily-respiratory-rate`, `daily-sleep-temperature-derivations`), leaves that column null, since not every device derives them — most Fitbits derive none of the optional four. The exception is a **malformed-request** `400` — one carrying `google.rpc.BadRequest` field violations, which only ever means the request we built is wrong. Those propagate and mark the connection `SyncError`, because resting HR anchors the HR baseline and a silent null there degrades alerting instead of reporting a fault. That guard is what surfaced the `resting-heart-rate` data-type bug, and then the camelCase `filter` member path (`dailyRestingHeartRate.date`, where the grammar wants the snake_case data type `daily_resting_heart_rate.date`), rather than letting either degrade the baseline unnoticed.

## Running Locally

```bash
# Navigate to worker project
cd src/Worker/CardiTrack.Worker

# Restore and run
dotnet run
```

The worker starts an HTTP listener (default port 8080, or `PORT` if set) for `/healthz`. Run logging is `Information`, which the local appsettings `Warning` baseline suppresses — set `Serilog__MinimumLevel__Default=Information` (and `Logging__LogLevel__Default=Information`, already the appsettings value) to see each run, as deployed dev already does (prod stays at `Warning` and suppresses these):
```
[06:00:00 INF] WearableSync triggered at 2026-03-12T06:00:00.000Z
[06:00:04 INF] WearableSync complete. Success: 12, Failed: 0.
```

## Deployment

### Docker

The real `Dockerfile` is multi-stage (SDK build → publish → chiseled runtime). Key points of the runtime stage — note the **aspnet** base (not `runtime`; Cloud Run needs the HTTP listener) and the cleared `ASPNETCORE_HTTP_PORTS`:

```dockerfile
# Runtime — chiseled Ubuntu: minimal, non-root (UID 1654), no shell.
# aspnet (not runtime) because Cloud Run health probes need /healthz over HTTP.
# "-extra" adds ICU and tzdata, needed for the Worker's time-zone-aware jobs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=1654:1654 --from=publish /app/publish .
EXPOSE 8080

# Clear the base image's ASPNETCORE_HTTP_PORTS so the app's UseUrls
# (bound to Cloud Run's PORT env var) is the sole binding source
ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "CardiTrack.Worker.dll"]
```

```bash
docker build -f src/Worker/CardiTrack.Worker/Dockerfile -t carditrack-worker .
docker run -e ConnectionStrings__DefaultConnection="..." -e PORT=8080 carditrack-worker
```

### Cloud Run

The worker deploys as the Cloud Run service `carditrack-<env>-worker` (Terraform module `deployments`, config in `infrastructure/main.tf`). Cloud Run supplies `PORT`; the startup probe hits `/healthz`.

### CI/CD (GitHub Actions)

The worker rides the shared app pipelines — there is no worker-specific workflow file:

- `.github/workflows/deploy-apps-dev.yml` — on changes under `src/Worker/**` (or shared projects): `build-worker` → `test-unit-worker` → `security-worker` → `push-worker-image` → `deploy-worker-dev` (`gcloud run deploy carditrack-dev-worker`).
- `.github/workflows/deploy-apps-prod.yml` — the promotion path to `carditrack-prod-worker`.

## Monitoring

Logging mirrors the API: **Serilog console sink** always, plus `AddApmShipping` (logs) and `AddApmTracing` (OTel traces) from `CardiTrack.Observability` when `Apm__Engine` + `Apm__Data` are configured. `/healthz` probe traffic is excluded from tracing. Both signals carry the release version — the `Version` log property and OTel's `service.version`, from `DeploymentInfo`. See the [API readme's APM section](../api/readme.md#apm-shipping-carditrackobservability) for the shared config contract and [release version on telemetry](../api/readme.md#release-version-on-telemetry-deploymentinfo) for how the version is stamped.

### Key log events

| Message | Level | Meaning |
|---|---|---|
| `WearableSync triggered at {Time}` | Info | Sync job started |
| `Synced DeviceConnection {Id}` | Info | One device synced OK |
| `No sync service registered for DeviceType {DeviceType}` | Warning | Provider not registered |
| `Failed to sync DeviceConnection {Id}` | Error | API/network failure |
| `WearableSync complete. Success: {S}, Failed: {F}` | Info | Sync run summary |
| `OrphanedOrganizationCleanup triggered at {Time}` | Info | Cleanup job started |
| `OrphanedOrganizationCleanup removed {Count} organizations older than {MinAge} ...` | Warning | Orphans found and deleted — a client bypassed the atomic setup endpoint; investigate |
| `OrphanedOrganizationCleanup complete. Nothing to remove.` | Info | Cleanup no-op run |
| `DeviceSyncAudit triggered at {Time} for a sample of {SampleSize}` | Info | Audit run started |
| `Audit pull failed for DeviceConnection {Id}` | Warning | Wide-window re-fetch failed — measurement precision lost, no data or status affected |
| `DeviceSyncAudit complete. Sampled: {S}, audited: {A}, failed: {F}` | Info | Audit run summary |

## Related Documentation

- [API Documentation](../api/readme.md)
- [Web Dashboard Documentation](../web/readme.md)
- [Mobile App Documentation](../mobile/readme.md)
- [Infrastructure Guide](../../infrastructure.md)

---

**Last Updated:** August 13, 2026
