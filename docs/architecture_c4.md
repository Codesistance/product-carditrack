# CardiTrack — C4 Architecture

> The system as **built and deployed on 2026-08-10** — dev environment, GCP `carditrack-490120`,
> `europe-west2`. Planned-but-absent elements (push dispatch, trend interpretation, prod
> MedGemma) are marked *(planned)*; everything else on these diagrams exists and runs.
> Levels follow the [C4 model](https://c4model.com): Context → Containers → Components.
> For the manual operations behind this picture see the
> [production setup runbook](technical/production_setup_runbook.md); for the AI design,
> [llm_design.md](llm_design.md).

## Level 1 — System Context

The wearer never logs in (product decision 2026-08-10): they wear the device; the family
watches. Self-monitoring is not the product.

```mermaid
C4Context
  title CardiTrack - System Context

  Person(caregiver, "Caregiver / Family", "Monitors a loved one's heart health; receives alerts and digests")
  Person(staff, "Org Admin / Staff", "Care-home scenario: manages members and alerts")
  Person_Ext(wearer, "Wearer", "Elderly person wearing the device. Never authenticates - not a user of the software")

  System(carditrack, "CardiTrack", "Early-warning heart monitoring for families: sync, baselines, AI assessment, alerts, digests")

  System_Ext(health, "Google Health API", "Wearable data (Fitbit, Pixel Watch): polling + registered webhook notifications")
  System_Ext(auth0, "Auth0", "Caregiver identity: email + social login")
  System_Ext(gemini, "Gemini 2.0 Flash", "General (non-medical) AI - never receives health data")
  System_Ext(push, "FCM / APNs (planned)", "Push delivery - arriving from a separate workstream")
  System_Ext(dd, "Datadog", "APM traces + logs (engine wired; tokens pending)")

  Rel(wearer, health, "Device syncs to")
  Rel(caregiver, carditrack, "Uses", "mobile + web")
  Rel(staff, carditrack, "Uses", "web")
  Rel(carditrack, health, "Polls 10-min + receives webhooks", "OAuth per connection")
  Rel(carditrack, auth0, "Authenticates via")
  Rel(carditrack, gemini, "Non-medical generation only")
  Rel(carditrack, dd, "Ships telemetry to")
  Rel(carditrack, push, "Will dispatch via")
```

**The one boundary that explains most of the design:** health data goes only to the
**in-project MedGemma** (a container below, not an external system); Gemini is wired so that
hosts holding health data physically cannot reach it (`AddMedicalAiServices` registers no
public-provider key — DPIA A5).

## Level 2 — Containers

```mermaid
C4Container
  title CardiTrack - Containers (dev, all on GCP europe-west2)

  Person(caregiver, "Caregiver / Family")

  System_Boundary(ct, "CardiTrack") {
    Container(mobile, "Mobile App", ".NET MAUI", "Family's primary surface (M1 screens)")
    Container(web, "Web App", "Blazor (Razor Components, interactive server)", "Browser surface; Auth0 login pending")
    Container(api, "API", "ASP.NET Core", "REST backbone: members, alerts, insights, digests, chat, reports")
    Container(worker, "Worker", ".NET background host", "ALL non-AI jobs: 10-min sync, daily baselines, partition retention, inactivity + statistical alerts")

    Container_Boundary(pipe, "AI pipeline (the sanctioned exception to the Worker rule)") {
      Container(rcv, "HealthWebhookReceiver", "Cloud Run service, public", "Authenticates Subscriber secret, drops verification probes, forwards raw to Pub/Sub")
      Container(jobs, "PipelineJobs", "Cloud Run jobs x3, one image", "--job digest (hourly) | aggregate (5-min) | assess (5-min offset)")
      Container(medgemma, "MedGemma", "Ollama on Cloud Run, CPU, internal-only", "Private medical model, Q4_K_M; scale-to-zero")
    }

    ContainerDb(sql, "Cloud SQL PostgreSQL", "Private IP", "System of record + partitioned time-series: GranularMetricHours, MetricRollupsHourly, DigestEntries, RealtimeAssessments")
    ContainerQueue(pubsub, "Pub/Sub realtime topic", "Pull subscription", "Raw notification buffer, at-least-once")
  }

  System_Ext(health, "Google Health API")
  System_Ext(auth0, "Auth0")
  System_Ext(gemini, "Gemini 2.0 Flash")

  Rel(caregiver, mobile, "Uses")
  Rel(caregiver, web, "Uses")
  Rel(mobile, api, "JSON/HTTPS", "Auth0 JWT")
  Rel(web, api, "JSON/HTTPS")
  Rel(api, sql, "EF Core")
  Rel(api, medgemma, "Insights, chat, reports (medical)")
  Rel(api, gemini, "General AI only - no health data")
  Rel(mobile, auth0, "Login")
  Rel(worker, sql, "Sync writes, baselines, retention")
  Rel(worker, health, "Polls every 10 min", "OAuth")
  Rel(health, rcv, "Webhook notifications", "shared secret, registered 2026-08-10")
  Rel(rcv, pubsub, "Publishes raw body")
  Rel(jobs, pubsub, "aggregate: drains")
  Rel(jobs, health, "aggregate: targeted re-fetch (notify-then-fetch)")
  Rel(jobs, sql, "digest + assessment writes, granular reads")
  Rel(jobs, medgemma, "digest + assess prompts")
```

**Placement rule (CLAUDE.md, binding):** non-AI background jobs and DB polling live only in
the **Worker**; the AI pipeline (webhook aggregation, SSA, MedGemma calls, severity routing,
digests) lives only on **GCP Cloud Run + Pub/Sub** — and must not host non-AI jobs. That is
why inactivity detection (no AI call) is a Worker cron even though it serves the pipeline's
story, and why the digest job (an LLM process) is not a Worker cron even though it is scheduled
background work.

## Level 3 — Components: the AI pipeline

```mermaid
C4Component
  title AI pipeline - Components

  Container_Boundary(rcvb, "HealthWebhookReceiver") {
    Component(handler, "WebhookNotificationHandler", "Minimal API", "Constant-time secret check over the full Authorization header; 200 authorized / 401 not; drops {type: verification} probes; forwards everything else raw")
  }

  Container_Boundary(jobsb, "PipelineJobs (one image, --job dispatch)") {
    Component(digest, "DigestGenerationService", "--job digest, hourly", "Recomputes a member's summary once their readings have moved past the last one; describes their local day in progress; every generation kept as history; no summary from silence")
    Component(drain, "NotificationDrainService", "--job aggregate, 5-min", "Pulls batches, hunts users/{id}, maps healthUserId to DeviceConnection, runs the standard targeted sync. Ack = nothing still needs a retry")
    Component(assess, "RealtimeAssessmentService", "--job assess, 5-min offset", "Latest 60-min HR window (>=45 min covered), dedup by (member, windowStart) - an unmoved window costs no inference")
    Component(ssa, "SsaDecomposition", "Application, dependency-free", "Lag-covariance + Jacobi eigen: trend + oscillation + noise residual; deviation in noise-RMS units")
    Component(parser, "AssessmentSeverityParser", "Application", "Strict closing 'Severity:' line only; critical/high/medium/low -> red/orange/yellow/green; unparseable NEVER alerts")
    Component(blocks, "MedicalPromptBlocks", "Shared prompt hygiene", "Age/sex/notes - never name or id; injection-framed caregiver notes")
  }

  ContainerDb(sql2, "Cloud SQL", "", "GranularMetricHours -> RealtimeAssessments, DigestEntries, Alerts")
  Container(mg, "MedGemma", "", "")
  ContainerQueue(ps, "Pub/Sub", "", "")

  Rel(handler, ps, "Raw notification")
  Rel(drain, ps, "Pull + ack")
  Rel(assess, ssa, "Decompose HR window")
  Rel(assess, parser, "Parse verdict")
  Rel(assess, blocks, "Member context")
  Rel(digest, blocks, "Member context")
  Rel(assess, mg, "CARDITRACK_REALTIME_ASSESSMENT_PROMPT")
  Rel(digest, mg, "CARDITRACK_FAMILY_DIGEST_PROMPT")
  Rel(assess, sql2, "Upsert assessment; red/orange -> HeartRate alert (insert-claim arbiter, one unresolved per member)")
  Rel(digest, sql2, "Upsert digest")
  Rel(drain, sql2, "Targeted sync writes")
```

## Level 3 — Components: the Worker

```mermaid
C4Component
  title CardiTrack.Worker - Components (cron via CronBackgroundService)

  Container_Boundary(wb, "CardiTrack.Worker") {
    Component(sync, "WearableSyncWorker", "10-min", "Trailing-window poll per due connection; granular ingestion + backfill run outside the sync success envelope")
    Component(base, "BaselineCalculationWorker", "daily 02:30", "30/60/90-day PatternBaselines + provisional 7/14-day windows (provisional never alerts)")
    Component(part, "PartitionMaintenanceWorker", "hourly", "Creates partitions ahead; retention = partition drop: granular 90d, rollups 13mo, digests 12mo, assessments 90d. Never drops what it did not name")
    Component(inact, "InactivityDetectionWorker", "15-min", "Silence = no granular readings >2h in waking hours on the anchor clock; one yellow device-check alert, resolve to re-arm")
    Component(stat, "StatisticalAlertWorker", "15-min offset", "R1 engine: 5 rules vs established 30-day baseline; null is never zero; remedy-scoped cooldowns (HeartRate type-scoped across producers)")
    Component(audit, "DeviceSyncAuditWorker", "weekly", "Sampled sync-integrity audit")
  }

  ContainerDb(sql3, "Cloud SQL", "", "")
  System_Ext(health2, "Google Health API", "")

  Rel(sync, health2, "OAuth polling + identity capture")
  Rel(sync, sql3, "DeviceActivityLogs -> ActivityLogs merge; GranularMetricHours")
  Rel(base, sql3, "PatternBaselines")
  Rel(part, sql3, "DDL: create ahead / drop expired")
  Rel(inact, sql3, "Inactivity alerts (rule: device_silence)")
  Rel(stat, sql3, "Alerts (rules: activity_decline, irregular_sleep, elevated_heart_rate, no_morning_activity, long_term_trend)")
  Rel(audit, sql3, "Audit findings")
```

## Code level (C4 L4) — the dependency rule

Not diagrammed per C4 practice; the compiler enforces it. Clean Architecture with four
composition roots (**Web, API, Worker, PipelineJobs** — every new repository registers in all
four):

```
Domain          -> references nothing
Application     -> Domain only, zero NuGet packages (SSA and the alert rules live here, dependency-free)
Infrastructure  -> Application + Domain (EF Core, Npgsql, provider clients, MedGemma/Gemini clients)
Hosts           -> composition roots only; HealthWebhookReceiver deliberately references
                   neither Application nor Infrastructure - no business logic in the one
                   container the internet can reach
```

## Keeping this current

This document states what exists, so it changes when reality does: update the affected level
in the same PR that lands the architectural change (new container, new external system, new
Worker/pipeline component, placement-rule exception), the same discipline as the
[manual-ops ledger](technical/production_setup_runbook.md).
