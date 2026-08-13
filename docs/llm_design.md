# LLM Design — CardiTrack

> **STATUS — read this first**
>
> - **Built today:** MedGemma (Ollama-served `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` on Cloud Run, enabled in dev, scale-to-zero) as the **Medical** AI provider and **Gemini 2.0 Flash** as the **General** provider, consumed by `GenerativeAiService`, `MedicalAiService`, `HealthInsightService`, and `ReportGenerationService` and surfaced through the API's **chat, insights, and reports** endpoints (`ChatController`, `InsightsController`, `ReportsController`). Insight prompts carry a **member context block** (age, sex, caregiver notes — never name or id) and switch by baseline state: a **learning-phase variant** while no baseline exists at all, a **provisional variant** while only a short-window (7/14-day) baseline does, and the full trend prompt once the 30-day baseline lands. The **family summary** is the first *background* LLM process: a Cloud Run job (`carditrack-<env>-pipeline-jobs`, half-hourly Cloud Scheduler, dev only) recomputes a plain-language summary per member whenever their readings have moved since the last one and their previous summary is at least 20 minutes old, via MedGemma, and appends it — every generation is kept — for `GET /api/v1/insights/members/{id}/digest` — wired through `AddMedicalAiServices`, so the job carries no public-provider key at all. Ingestion is **10-minute polling** of the Google Health API by `WearableSyncWorker` in `CardiTrack.Worker`.
> - **Real-time path (built, dev):** the webhook receiver and 5-minute aggregator are live with the **Subscriber registered against Google (2026-08-10)** — notifications flow end to end — and the **real-time assessment** now runs end to end off the granular store: a twice-hourly Cloud Run job (`carditrack-<env>-pipeline-jobs-assessor`) takes each member's latest hour of heart rate, decomposes it with **SSA** (native .NET, `SsaDecomposition` in Application), asks MedGemma for a severity verdict, stores it in the partitioned `RealtimeAssessments` table (90-day retention by partition drop), and routes red/orange verdicts to `Alert` rows — one unresolved heart-rate alert at a time.
> - **Environmental-context enrichment (built, dev, consent-gated off by default):** for GPS-equipped wearables, a fourth Cloud Run job (`carditrack-<env>-pipeline-jobs-enricher`, `--job enrich`) looks up ambient temperature and air quality (Google Maps Platform Weather + Air Quality APIs) for a member's GPS-tagged exercise sessions and folds the derived values into the real-time assessment prompt. Gated on a new per-member `CardiMember.EnvironmentalContextConsentGranted` flag — default `false`, the sole candidate filter — and on a new Restricted OAuth scope (`googlehealth.location.readonly`) not yet requested from Google. **Raw GPS coordinates are never persisted**: the enrich job reads a session's coordinates only long enough to call the environmental APIs, and only the resulting temperature/AQI values are stored, in the partitioned `EnvironmentalReadings` table (90-day retention). Noise/sound-level context was scoped out — no location-queryable data source exists at production grade.
> - **Design decisions, 2026-08-10:** the **LSTM is dropped**, not parked. Personalization comes through the context window instead: deterministic .NET computes every number (SSA, baselines, multi-horizon rollups), MedGemma interprets them, and clinical reference ranges are **pinned in a curated table injected into the prompt** — never recalled from model weights, so every assessment's yardstick is auditable. With it go the per-user model files, the Python training job, ONNX, and the calibrated numeric risk scores (0–100 fall-risk etc.) — the predictive path becomes the **trend interpretation** design below. **Wearer-audience features are permanently descoped**: wearers never log in; self-monitoring is not the product.
> - **Still design-only:** trend interpretation (waits on the R1 statistical engine's baselines), push dispatch (FCM/APNs arrives from a separate workstream; this pipeline only wires dispatch once it lands).

## Overview

CardiTrack uses MedGemma as its inference model for cardiovascular analysis of wearable data from up to 10,000 wearable devices (Fitbit, Pixel Watch, and other sources connected through the Google Health API). The AI pipeline runs two parallel paths: a real-time anomaly detection path (5-minute windows, SSA pre-processing → MedGemma severity verdict) and a daily interpretive path (computed trend features + pinned reference ranges → MedGemma → family-facing narrative). In both, the division of labour is fixed: **deterministic code computes every number; MedGemma only ever interprets them.** All pipeline logic runs on **Cloud Run services and jobs (CPU), scheduled by Cloud Scheduler**, in the same GCP project as the rest of the platform (`carditrack-490120`, `europe-west2`).

---

## Model

| Property | Value |
|----------|-------|
| Model | `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` (Ollama tag; MedGemma 1.5 4B instruction-tuned) |
| Parameters | 4B |
| Quantisation | Q4_K_M — the one tag the local, dev and prod configs all resolve to, so an assessment made in one environment means the same thing in another. Prod's service is not deployed yet (see the service map above); when it is, it serves these weights |
| Type | Multimodal instruction-tuned |
| Serving | Ollama on Cloud Run (CPU) — see [Infrastructure](#infrastructure) |

MedGemma 4B was chosen over the 27B variant for cost and latency reasons — at 4B parameters it runs on CPU-only Cloud Run today and would fit on a single T4 GPU (~8 GB in float16) if GPU serving is ever needed. It delivers strong accuracy on medical text reasoning and EHR understanding, both directly applicable to structured wearable time-series data.

---

## Infrastructure

### Service map (GCP)

| Service | Role | Status |
|---------|------|--------|
| **Cloud Run — `carditrack-<env>-medgemma`** | MedGemma inference via Ollama (CPU) | **Built** — enabled in dev; prod leaves `medgemma_image` empty, so the service does not exist there yet |
| **Cloud Run services/jobs + Cloud Scheduler** | All pipeline logic — webhook receiver, aggregation, SSA pre-processing, assessment, environmental enrichment, trend interpretation, digest, push dispatch | **Built (dev):** digest + aggregator + assessor + enricher jobs (`carditrack-<env>-pipeline-jobs[-aggregator|-assessor|-enricher]`) and the webhook receiver service, all gated on their enable flags; trend interpretation and push dispatch are target design |
| **Cloud Pub/Sub** (`carditrack-<env>-realtime`) | Wearable raw event stream buffer | Topic + pull subscription provisioned in **dev and prod** (`enable_pubsub`); the receiver publishes and the aggregator drains it in dev, carrying **live registered traffic** since 2026-08-10 |
| **Cloud SQL PostgreSQL (existing instance)** | OAuth tokens (encrypted AES-256-GCM in `DeviceConnections`), user profiles, sensitivity settings, family relationships — the transactional system of record (see [infrastructure.md](./infrastructure.md#storage-boundary)); plus typed partitioned tables for AI results (below) | Built — core schema plus `DigestEntries`, `RealtimeAssessments`, and `EnvironmentalReadings` |
| **FCM / APNs** | Push routing for alerts and digests | **Planned — no push infrastructure exists yet** |
| **Google Maps Platform** (Weather + Air Quality APIs) | Ambient temperature/air-quality lookups for the enrichment job | **Built (code); key unprovisioned** — `Environmental:ApiKey` is required config, not yet in any environment's Secret Manager |
| **Secret Manager** | Google Health API OAuth client secret, `gemini-api-key`, `medgemma-service-url`, `Environmental:ApiKey` (planned) | Built |

Deliberate decision: AI results live **inside the existing Cloud SQL instance** rather than a separate document store — one data plane, one backup story, and family-read scoping can join directly against `UserCardiMembers`. (The original sketch said JSONB; the built tables are **typed and partitioned** — the granular-storage ADR's scale analysis showed typed columns beating JSONB key-bloat at volume.)

---

### Pipeline components: role breakdown (target design)

Each component is a Cloud Run service (event/HTTP-triggered) or Cloud Run job (Cloud Scheduler-triggered). All are CPU-only.

| Component | Trigger | Cadence | Purpose |
|-----------|---------|---------|---------|
| `HealthWebhookReceiver` | HTTP (Cloud Run service) | On event (~333/s peak) | **Built (dev)** — `carditrack-<env>-webhook-receiver`, gated on `enable_webhook_receiver`. Authenticates the Subscriber's shared secret (full `Authorization` header, constant-time), acknowledges with `200` (the status Google's verification handshake demands), forwards the **raw, unparsed** payload to Pub/Sub — notify-then-fetch means nothing downstream ever trusts it, and the one sanctioned peek drops Google's documented `{"type": "verification"}` probe instead of forwarding it. **Subscriber registered (dev, 2026-08-10)** — live notifications flow; the runbook's provisioning section records the working procedure |
| `WearableAggregator` | Cloud Scheduler | Every 5 min | **First increment built (dev):** `carditrack-<env>-pipeline-jobs-aggregator` drains the realtime subscription, maps each notification's `healthUserId` to its `DeviceConnection` (captured once per connection during sync via `GET /v4/users/me/identity`), and runs the standard targeted sync — same invariants, sooner; `LastSyncDate` stamping makes polling the fallback rather than a duplicate. The SSA → MedGemma → severity chain runs in the separate assessor job below rather than inline — the aggregator moves data, the assessor reads it, and either works without the other |
| `RealtimeAssessor` | Cloud Scheduler | Twice hourly, at :02 and :32 (offset from the aggregator) | **Built (dev):** `carditrack-<env>-pipeline-jobs-assessor` — for each member with fresh data, SSA over the latest 60-minute heart-rate window (≥45 covered minutes; window keyed by its start, so an unmoved window costs no inference), MedGemma assessment (`CARDITRACK_REALTIME_ASSESSMENT_PROMPT`), result to the partitioned `RealtimeAssessments` table. Works entirely off the granular store, so it functions on polling alone — webhook registration only makes it fresher. Reads a recent `EnvironmentalReading` (below) through the shared member-context composer, which now carries it into every medical prompt rather than this one alone — the assessor keeps its own three-hour staleness rule |
| `EnvironmentalEnricher` | Cloud Scheduler | Every 15–30 min (offset from the other jobs) | **Built (dev), consent-gated off by default:** `carditrack-<env>-pipeline-jobs-enricher` — for members with `CardiMember.EnvironmentalContextConsentGranted = true` (the sole candidate filter) and a connection granted the `googlehealth.location.readonly` scope, fetches GPS-tagged exercise sessions (Google Health API `exercise` data type + `exportExerciseTcx`), looks up ambient temperature, described conditions, humidity and air quality for each new session's coordinate (Google Maps Platform Weather + Air Quality APIs — conditions and humidity ride along on the current-conditions response the temperature lookup already makes, so they cost no extra call), and stores only the derived values in the partitioned `EnvironmentalReadings` table — **the raw coordinate is never persisted**, discarded immediately after the environmental lookup. Runs as its own job rather than inline in the aggregator: exercise sessions are sparse and don't need 5-minute freshness, and an external vendor call does not belong in the hot sync loop 10,000 devices go through every few minutes. The Google Maps Platform key and the exercise/GPS device-client methods are registered nowhere else in the process (`EnvironmentalServiceExtensions`), so no other host or job path can reach them |
| `SeverityRouter` | On new result row | On write | **First increment built (dev), inline in the assessor rather than a separate component:** the model's closing `Severity:` line is parsed strictly (critical/high/medium/low → red/orange/yellow/green; an unparseable answer is stored but routes nowhere — the model cannot page a family by mumbling), and red/orange verdicts create `Alert` rows with a one-unresolved-heart-rate-alert-at-a-time cooldown. Immediate push via FCM/APNs still waits on push infrastructure |
| `TrendInterpreter` | Cloud Scheduler | Daily (design) | Replaces the former LSTM predictive path (dropped 2026-08-10): reads the member's multi-horizon rollups + R1 baselines, computes trend features deterministically (moving averages, slopes, deviations — .NET, no ML), injects the **pinned clinical reference-range table**, and asks MedGemma for a family-facing trend narrative feeding digests/insights. No risk scores, no per-user models. The R1 statistical engine it reads from is built (`StatisticalAlertWorker` in the Worker); what remains is this interpretation job and its pinned reference-range table |
| `DigestGenerator` | Cloud Scheduler | **Built (dev):** half-hourly scheduler, regenerating for **whichever members' readings have moved since their last summary** — members whose data has not changed, and members summarised within the last 20 minutes, are skipped before any model call. The two gates decouple the cadence from the *inference* bill: the job can run often enough to catch a quiet member up quickly without regenerating a continuously-uploading one on every pass. They do not make the cadence cost-free, though — where MedGemma scales to zero a pass that finds any work at all pays a cold start at the full CPU allocation, and where it is kept warm (dev today, `medgemma_min_instances`) the cadence drives inference volume against an instance bill that runs regardless. **An alert raised or resolved since the last summary waives both** — that is the one change that rewrites what the summary should say, and making a caregiver wait out the floor to read it would be the floor working against its own purpose. A medium observation alone does not waive them; it rides the ordinary cycle | Summarises the member's local day in progress (their anchor timezone is the earliest-linked caregiver's `User.TimeZoneId`) → calls MedGemma (`CARDITRACK_FAMILY_DIGEST_PROMPT`, returning a short `headline` and the summary text) → **appends** to the partitioned `DigestEntries` table, whose key carries `GeneratedAtUtc` so every recomputation is kept as history (12-month retention by partition drop); read via `GET /api/v1/insights/members/{id}/digest` (current) and `GET /api/v1/insights/members/{id}/digests` (history). The same call may also propose **one short question to the family** when the readings would be clearer for an answer; it is stored as a `MemberQuestionnaire` (see `docs/execution/backend/api/questionnaires.md`) under two noise gates — one open question per member, and seven days since the last one was asked whatever became of it. Push delivery waits on FCM/APNs (arriving from a separate workstream); the family audience is the only audience — wearers never log in |
| `InactivityDetector` | ~~Cloud Scheduler~~ Worker cron | Every 15 min | **Built:** `InactivityDetectionWorker` in `CardiTrack.Worker` — this table originally drew it beside the pipeline, but it makes no AI call, and non-AI background jobs are Worker-exclusive per CLAUDE.md, so placement follows the rule, not the diagram. Silence means **no granular readings** (a sync that returns nothing is exactly the dead-battery case), measured on the member's anchor clock: >2 h without a minute during waking hours (07:00–22:00 local, effectively from 09:00 so the charger never trips the first alert of the day) raises one yellow `Inactivity` alert, suppressed until resolved |

> **Runtime note:** all pipeline components run on **.NET**, matching the rest of the platform, and every numeric stage (SSA, baselines, trend features) is native, dependency-free .NET. With the LSTM dropped (2026-08-10) there is no ONNX runtime, no TensorFlow, and no Python anywhere in the pipeline — the former `ModelRetrainer` training job is gone with it.
>
> **Timeout note:** `WearableAggregator` and `TrendInterpreter` are the longest-running components. Cloud Run jobs allow generous task timeouts (up to 24 h), so both are designed to process users in parallel batches and complete comfortably at 10 K users.

> **Ingestion:** `WearableSyncWorker` in `CardiTrack.Worker` polls the Google Health API on a **10-minute cron** — now the *fallback* behind the registered webhook path, and still the guarantee that nothing is lost when notifications lag. Each device writes its own raw row to `DeviceActivityLogs` (one per device per day), and those are merged into `ActivityLogs` — one row per CardiMember per day, which is the series every reader consumes. The merge coalesces each metric independently by device priority and never sums, so multiple wearables fill each other's gaps without double-counting. Each run re-fetches a short **trailing window** ending at today (`SyncLookbackDays`, default 3 complete days behind it) so the day in progress is visible and a day missed during an outage is recovered rather than lost; a connection becomes due on its own `SyncFrequencyMinutes`, with the cron setting only how often the worker looks. The registered webhook path triggers the same sync sooner; polling remains the safety net, never a duplicate (`LastSyncDate` stamping pushes the routine poll out).
>
> Note that this polling path writes **only** to Cloud SQL — it does not publish to Pub/Sub, by design. The topic below carries provider webhook notifications forwarded by `HealthWebhookReceiver`, not `ActivityLogs` egress from the Worker; the Worker stays free of AI-pipeline responsibilities (see `CLAUDE.md`).
>
> **Granular substrate (built):** the same worker-cadence pulls now also store minute-grain series — 1-minute heart rate and steps, active-zone minutes, ~5-minute SpO2 — as per-device hour vectors in the partitioned `GranularMetricHours` table, with per-member hourly rollups in `MetricRollupsHourly` and week/month views over the daily rows (see [granular_timeseries_storage.md](./technical/granular_timeseries_storage.md)). The moving-window read the SSA pre-processor needs (`IGranularMetricRepository.GetWindowAsync` — merged minute series over an arbitrary UTC hour range) is what the assessor job consumes today.

---

### MedGemma serving: what ships

MedGemma runs as the Cloud Run service `carditrack-<env>-medgemma`, provisioned by Terraform (`infrastructure/deployments/cloud_run.tf`) and deployed by CI:

| Property | Value |
|----------|-------|
| Platform | Cloud Run (**CPU** — no GPU) |
| Serving engine | **Ollama** (`ollama/ollama` base image; model baked in at build time) |
| Model tag | `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` — pinned in `src/Infrastructure/MedGemma/.model-version`, and the value `docker-compose.yml` and `AI__Private__Model` must both match |
| Resources | 4 vCPU / 16 Gi, `cpu_idle = false`, startup CPU boost |
| Scaling | Max **1 instance** (Ollama cannot safely multi-instance) |
| Ingress | Internal-only (VPC); port 8080 |
| Enablement | Service is created only when the `medgemma_image` tfvar is non-empty |

Deployment flow: the `deploy-medgemma` job in `.github/workflows/deploy-apps-prod.yml` deploys the image tag, then writes the resulting service URL to the Secret Manager secret `carditrack-<env>-medgemma-service-url`, which the API consumes as **`AI__Private__BaseUrl`**.

---

### Two AI systems: public and private

The API talks to two providers, and the difference between them is a boundary, not a preference.

| | **Private** (`AI:Private`) | **Public** (`AI:Public`) |
|---|---|---|
| Used by | Health insights (`HealthInsightService`) | Reports (`ReportGenerationService`), chat (`ChatController`) |
| Provider | **MedGemma only — fixed in code** | Chosen by `AI__Public__Kind`: `Gemini` or `Anthropic` |
| Where inference runs | In-project Cloud Run, IAM-authorised invokers | Off-estate, at the provider |
| Prompt content | Metrics, baselines, derived age and sex, free-text `MedicalNotes` | Metrics and alerts only — members are pseudonymised before the call |
| Configuration | `Model`, `BaseUrl`, `TimeoutSeconds` | `Kind`, `Model`, `ApiKey`, optional `BaseUrl`, `TimeoutSeconds`, `MaxOutputTokens` |

The private side has no provider selector. `AiServiceExtensions` constructs `MedGemmaClient` unconditionally for the medical slot, so no environment variable can route health data to an off-estate model — which is the control [the DPIA](./compliance/dpia.md) records for A5. Only where MedGemma lives and which weights it serves are configurable.

Every MedGemma call is fully observable client-side (the Ollama container is stock and carries no instrumentation): `MedGemmaClient` emits a GenAI-semconv span, duration/token metrics and a per-call log line through the `CardiTrack.Ai` source — token counts, durations, model names and error types only, never prompt text or model output, per the same DPIA constraint. Details in the [APM setup runbook](./technical/apm_setup_runbook.md).

The public side is deliberately swappable. Every provider implements the same `IExternalAiClient`, and `Kind` selects which one is built at startup; consumers see only `IGenerativeAiService`. Swapping providers is a tfvar change plus seeding the new key into the API-key secret — no rebuild. Config is validated at startup, so a bad `Kind`, a missing model or key, or a malformed URL fails the revision rather than the first caregiver's request.

**Adding a public provider** is two edits and a test: a client implementing `IExternalAiClient` in `ExternalClients/General/`, and a member on `PublicAiProviderKind` wired into the switch in `AiServiceExtensions`. Nothing downstream changes.

| Provider | Transport | Endpoint default | Notes |
|----------|-----------|------------------|-------|
| `Gemini` | `HttpClient` (`generateContent`) | `https://generativelanguage.googleapis.com` | Key sent as the `x-goog-api-key` header |
| `Anthropic` | Official `Anthropic` .NET SDK (Messages API) | `https://api.anthropic.com` | `MaxOutputTokens` is mandatory on this API; the SDK owns its own transport |

> **GPU scaling option (future):** if CPU latency becomes a bottleneck at scale, the same model can be served by vLLM on a single NVIDIA T4 (16 GB, float16 — the 4B model fits with KV-cache headroom), with `--enable-prefix-caching` exploiting the fixed system prompts, autoscaling on HTTP concurrency. This would require HuggingFace weights access (Health AI Developer Foundations terms) and a GPU-capable runtime (e.g. Cloud Run GPU or GKE Autopilot). Provisioning would be added to the existing Terraform — no imperative scripts.

---

### AI results: PostgreSQL tables (built as typed + partitioned; the original JSONB sketch below is kept for lineage)

AI outputs are derived data in the **existing Cloud SQL instance** — regenerable, never authoritative. All tables are keyed by `wearer_user_id` for efficient per-user queries, with a JSONB payload column.

| Table | Key columns | JSONB payload | Retention |
|-------|-------------|---------------|-----------|
| `realtime_results` | `wearer_user_id`, `window_start`, `severity` | `medgemma_output`, `anomaly_scores` | **Built as the typed, day-partitioned `RealtimeAssessments` table** (`CardiMemberId`, `WindowStartUtc` PK; SSA features, model output and routed severity as columns — typed rather than JSONB, per the granular-storage ADR) — 90 days by partition drop |
| `prediction_cards` | ~~`wearer_user_id`, `date`~~ | ~~`risk_scores`, `confidences`, `medgemma_output`~~ | **Descoped 2026-08-10** with the LSTM — trend interpretation writes narrative into digests/insights, not risk-score rows |
| `trend_aggregates` | `wearer_user_id`, `date` | `resting_hr_7d_ma`, `hrv_7d_ma`, `sleep_score_7d_ma` | 2 years |
| `digest_log` | `wearer_user_id`, `date`, `audience` | `digest_text` | 1 year |
| *(net-new)* | — | — | **Built as the typed, day-partitioned `EnvironmentalReadings` table** (`CardiMemberId`, `SessionStartUtc` PK; `TemperatureCelsius`, `AirQualityIndex`, `AirQualityCategory` as columns — no latitude/longitude column exists on this table, structurally) — 90 days by partition drop |

Row expiry runs in the same scheduled cleanup machinery as other retention jobs (a Worker/Cloud Run job), since PostgreSQL has no document TTL.

---

### Deployment

Everything is provisioned by the existing Terraform (`infrastructure/` — see the [operator guide](../infrastructure/README.md)):

- **MedGemma service** — `deployments/cloud_run.tf` (gated on `medgemma_image`); image built from `src/Infrastructure/MedGemma/Dockerfile`
- **Pub/Sub topic + subscription** — `deployments/pubsub.tf` (gated on `enable_pubsub`; prod only today)
- **Secrets** — `deployments/secret_manager.tf` (`gemini-api-key`, `medgemma-service-url`)
- Pipeline components (webhook receiver, aggregator, scheduler jobs) will be added to the same Terraform as they are built

No imperative CLI provisioning scripts are used.

---

## AI Pipeline Overview

CardiTrack operates two parallel AI paths with distinct cadences and purposes:

```
┌─────────────────────────────────────────────────────────────┐
│                    REAL-TIME PATH (5-min)                   │
│                                                             │
│  Wearable event → Pub/Sub → Aggregator → SSA → Assessment   │
│  → MedGemma (anomaly) → Severity router → Alert / Digest   │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│               PREDICTIVE PATH (daily batch)                 │
│                                                             │
│  Cloud SQL (30-90 day history) → Feature aggregator         │
│  → Trend features (computed) → MedGemma (interpretation)   │
│  → Prediction card → Wearer + Family digest                 │
└─────────────────────────────────────────────────────────────┘
```

The real-time path answers: *"Is something wrong right now?"*
The predictive path answers: *"Is something likely to go wrong in the next 24–72 hours?"*

---

## Data Ingestion Pipeline (Real-Time, target design)

> **API note:** the legacy Fitbit Web API is decommissioned in September 2026. All device data access uses the **Google Health API** (`health.googleapis.com`) — a single integration covering Fitbit devices, Pixel Watch, and connected third-party sources, with per-reading source attribution. Webhook subscriptions (including heart rate) replace the old Fitbit Subscriptions API; notifications carry the user, data type, and changed interval, and the pipeline fetches the data itself (notify-then-fetch).

```
Wearable devices (up to 10,000 — Fitbit, Pixel Watch, third-party)
  ↓
Google Health API webhooks (push notifications — no polling)
  ↓
Cloud Run service (HTTP) — verifies webhook auth, forwards notification
  ↓
Cloud Pub/Sub (carditrack-prod-realtime)
  ↓
Cloud Run job (Cloud Scheduler, every 5 min) — fetches changed data, aggregates per user
  ↓
SSA-LSTM pre-processor — denoises signal, extracts trend features
  ↓
MedGemma service (Ollama on Cloud Run, internal VPC)
  ↓
Cloud SQL typed partitioned tables (results store)
```

---

## SSA Pre-Processing Layer

Before each window is sent to MedGemma, raw wearable time-series data passes through Singular Spectrum Analysis. SSA decomposes each metric into **Trend**, **Oscillation**, and **Noise** components; MedGemma receives the denoised trend values rather than raw averages, improving anomaly sensitivity.

> **Built:** `SsaDecomposition` in `CardiTrack.Application`, dependency-free .NET (lag-covariance + Jacobi eigen-decomposition; window 30, trend + 2 oscillation components, noise as the residual). The deviation check compares the **actual latest reading against the SSA trend, in units of the noise RMS** — the member's own jitter as the yardstick; the assessor's stored `HrDeviationScore` is exactly this.
>
> **The LSTM forecast that used to sit beside SSA was dropped 2026-08-10.** Personalization beyond the SSA window comes from the R1 baselines and rollups fed through the prompt (see Trend Interpretation), not from a trained per-member forecaster — no training pipeline, no per-member model files, no calibrated risk scores.

### Role in the pipeline

| Stage | Input | Output |
|-------|-------|--------|
| SSA decomposition | Raw intraday time-series | Trend + Oscillation components per metric |
| Deviation check | Actual latest reading vs. SSA trend | Deviation score in noise-RMS units |
| MedGemma prompt | Denoised trend + deviation yardsticks | Cardiovascular assessment + severity verdict |

### Google Health API Data Type → SSA-LSTM Input Mapping

The Google Health API consolidates the legacy per-endpoint surface into **data types** queried via `list` (intraday/granular) and `rollUp`/`dailyRollUp` (summaries) at `https://health.googleapis.com`.

Methods follow the v4 REST shape: `GET /v4/users/me/dataTypes/{type}/dataPoints` (list) and `POST .../dataPoints:dailyRollUp` (daily summary; heart-rate/active-minutes/total-calories rollups max 14-day range, others 90 days).

A `dailyRollUp` body takes `range` as a **`CivilTimeInterval`** — closed-open, with `start`/`end` each a **`CivilDateTime`**, meaning the calendar date nests under `date` (`{"start": {"date": {"year": …, "month": …, "day": …}}}`) and `time` is omitted to mean midnight. A bare `{year, month, day}` at `range.start` is rejected with `INVALID_ARGUMENT` field violations — the shape `FitbitApiClient` sent until it was corrected against the live API.

`list` takes its window as an AIP-160 `filter` query parameter instead, and **each filterable field admits exactly one literal format**: physical-time fields (`{type}.interval.start_time`, `sleep.interval.end_time`) require an RFC-3339 instant (`"2026-08-05T00:00:00Z"`), while their civil-time siblings (`{type}.interval.civil_start_time`, `sleep.interval.civil_end_time`) require an ISO 8601 civil time in one of exactly two forms — date-only `2026-08-05` or date-time `2026-08-05T00:00:00`, never with a `Z` or offset suffix (Google's reference writes this as `yyyy-MM-dd[THH:mm:ss]`, where the brackets denote an optional segment, not literal characters). Mixing the two is not coerced — a bare date against `sleep.interval.end_time` returns a 400 with reason `INVALID_DATA_POINT_FILTER` and `detailedReasons: INVALID_DATA_POINT_FILTER_TIMESTAMP_FORMAT`, which is exactly what `FitbitApiClient` sent until it was corrected against the live API. Only `>=` and `<` are supported, and sleep is filterable on **end time only** (sessions are attributed to the day they ended on).

Prefer the **civil** variants throughout. Physical-instant filtering buckets by UTC day while every `dailyRollUp` above buckets by the wearer's local day, so for a wearer west of Greenwich a late-evening sleep session would land in the next day's snapshot while the same night's steps stayed in the current one — a silent per-timezone misalignment in the SSA input series, not an error. A unit test pins the emitted sleep filter string.

| Metric | Data type / method | Sampling Rate | SSA Input |
|--------|--------------------|----|-----------|
| Heart Rate (intraday) | `heart-rate` — `list` | 1-min intervals | Primary time-series for SSA decomposition |
| Resting Heart Rate | `daily-resting-heart-rate` — `list` | Daily scalar | Baseline anchor for HR trend |
| HRV (RMSSD) | `daily-heart-rate-variability` — `list` (granular: `heart-rate-variability` — `list`) | Daily scalar | Secondary series |
| SpO2 (intraday) | `oxygen-saturation` — `list` | ~5-min intervals | Upsample to 1-min via forward-fill before SSA |
| Steps (intraday) | `steps` — `list` | 1-min intervals | Used as activity context feature alongside HR |
| Active Zone Minutes | `active-zone-minutes` — `list` | 1-min intervals | Exogenous input to LSTM |
| Skin Temperature | skin-temperature data type — `dailyRollUp` | Daily scalar (nightly) | Early-warning feature; include when available |
| Sleep Stages | `sleep` — `list` (session-shaped) | Daily summary | Context feature for next-day recovery model |

> **Not every data type supports every method.** A type's *record type* decides: Interval and Sample types (`steps`, `distance`, `active-minutes`, `total-calories`, `floors`, `sedentary-period`, `heart-rate`, `oxygen-saturation`) roll up; **Daily** types (`daily-resting-heart-rate`, `daily-heart-rate-variability`, `daily-oxygen-saturation`, `daily-vo2-max`, `daily-respiratory-rate`, `daily-sleep-temperature-derivations`) are already one point per day and support only `list`/`reconcile`; Session types (`sleep`, `electrocardiogram`, `irregular-rhythm-notification`) support `list`/`get`. A rollup can also be the wrong method for a rollable type: `dailyRollUp`'s union has no `oxygenSaturation` member, so SpO2 min/max exist only in the sample series and `FitbitApiClient` lists it rather than rolling it up. Asking a Daily type for a rollup returns 400 `INVALID_ARGUMENT` with reason `INVALID_PARENT_DATA_TYPE_COLLECTION` — `FitbitApiClient` rolled up a `resting-heart-rate` that is neither a real type ID nor rollable, which failed every wearable sync until it was corrected. A Daily type's window is a `filter` on its own `date` field, using the same ISO literal and `>=`/`<` grammar as the civil-time fields above.
>
> **`filter` member paths are snake_case**, and they name the *data type*, not the response's union member: the documented patterns are `{daily_summary_data_type}.date`, `{interval_data_type}.interval.civil_start_time`, `{sample_data_type}.sample_time.civil_time` and (sleep-specific) `sleep.interval.civil_end_time`. So it is `daily_resting_heart_rate.date >= "2026-08-05"` — the camelCase `dailyRestingHeartRate.date` that keys the *response* is rejected with `INVALID_DATA_POINT_FILTER` / `INVALID_DATA_POINT_FILTER_DATA_TYPE_RESTRICTION` ("does not match any data type"), which failed every wearable sync until it was corrected. Single-word types like `sleep` spell the same either way, so they prove nothing about the convention.
>
> Rollup responses carry a union value per data type, and its fields are named **`{field}{Aggregation}` in camelCase** — `steps.countSum`, `heartRate.beatsPerMinuteMin/Max/Avg`, `distance.millimetersSum`, `totalCalories.kcalSum`, `floors.countSum`. Units are the schema's own — distance is **millimetres**, not metres. `active-minutes` is the exception with no scalar total, returning `activeMinutesRollupByActivityLevel[]`, of which CardiTrack sums the `MODERATE` and `VIGOROUS` levels to match Fitbit's classic figure. Sleep carries `summary.stagesSummary[]` keyed by stage `type` (`DEEP`/`LIGHT`/`REM`/`ASLEEP`/`AWAKE`/`RESTLESS`) plus `minutesAsleep`/`minutesAwake`/`minutesInSleepPeriod`; there is **no efficiency field**, so CardiTrack derives it as asleep ÷ sleep period.
>
> **Three encodings hide behind those names, and every one fails silently if missed.** `int64` fields serialise as JSON **strings** under proto3 JSON (`"countSum": "9423"`), so a numeric-only parse reads them as absent. `Duration` fields serialise as strings with a mandatory **`s` suffix** (`"durationSum": "28800s"`) — `sedentary-period` is the one here, and parsing it as a bare number returns null on every wearer. And the enum members are per-schema: `ActiveMinutesRollupByActivityLevel.activityLevel` is `LIGHT`/`MODERATE`/`VIGOROUS`, *not* the `SEDENTARY`/`LIGHTLY_ACTIVE`/`MODERATELY_ACTIVE`/`VERY_ACTIVE` of the neighbouring `activity-level` type — borrowing that spelling matched no level and summed to zero. The **discovery document** (`https://health.googleapis.com/$discovery/rest?version=v4`, public, no auth) is the authority for all three: it gives each field's `format` and each enum's members, which the prose reference does not. Check it before adding a field, because `int64`, `google-duration` and `double` all look like "a number" in an example payload.
>
> **An absent rollup bucket is not a zero.** Google's own guidance is that a date missing from a rollup response means the device was not worn or has not synced, while a present `"countSum": "0"` is a true zero. CardiTrack keeps the two distinct all the way to the column: every activity metric is nullable, and `FitbitApiClient` never coalesces. Collapsing them would corrupt both consumers — the multi-device merge takes the first non-null value, so a manufactured 0 from a higher-priority device beats another device's genuine reading, and the baseline averages unsynced days in as stillness, which is indistinguishable from the inactivity the product exists to detect.
>
> An earlier snake_case reading of the convention (`count`, `beatsPerMinute_avg`, `meters_sum`) matched nothing and returned **zeros rather than errors** — the whole class of bug this paragraph exists to prevent. All names, formats and enum members above are now checked against the discovery document and pinned by unit tests, but a schema cannot prove a field is *populated* for a given wearer's device: [`tools/HealthApiProbe`](../tools/HealthApiProbe/README.md) checks that against a live account.

### SSA Parameters

| Parameter | Recommended Value | Rationale |
|-----------|------------------|-----------|
| `window_size` (L) | `30` | 30-minute lag window captures ~2 cardiac cycles and one activity micro-burst |
| Number of components | `3` (Trend + 2 Oscillations) | Isolates circadian rhythm + short activity oscillation from noise |

### Implementation

> **Reference implementation** (Python, for algorithm clarity only — nothing Python runs in production). The production pre-processor is `SsaDecomposition`, implemented natively in .NET.

```python
# pip install pyts tensorflow pandas numpy

from pyts.decomposition import SingularSpectrumAnalysis
import numpy as np

def preprocess(hr_series: list[float], window_size: int = 30) -> dict:
    """
    Decomposes a 1-minute HR time-series and returns trend + anomaly score.
    hr_series: list of bpm values, length >= window_size * 2
    """
    ssa = SingularSpectrumAnalysis(window_size=window_size, groups=[[0], [1, 2]])
    components = ssa.fit_transform([hr_series])  # shape: (n_groups, n_samples)
    trend = components[0]          # Trend component
    oscillation = components[1]    # Short-term oscillation
    noise = np.array(hr_series) - trend - oscillation

    return {
        "trend_last": float(trend[-1]),
        "oscillation_last": float(oscillation[-1]),
        "noise_rms": float(np.sqrt(np.mean(noise ** 2))),
    }
```

> **Deployment note:** SSA runs inside the twice-hourly assessor job (CPU only — no GPU required); a 60-sample decomposition is sub-millisecond, negligible against the window budget.

### Provisioning the webhook subscriber

The v4 discovery document defines the surface, and the live API has since sharpened it
(2026-08-10 registration work — full commands in the
[production setup runbook](./technical/production_setup_runbook.md) §7): a **Subscriber**
(`POST /v4/projects/carditrack-devices-{env}/subscribers` — the **devices** project, not
infra) registers our `endpointUri` and an `endpointAuthorization.secret` — the **full
`Authorization` header value, scheme included**. `subscriberConfigs` takes **`dataTypes`**
(an array, kebab-case — the singular `dataType` the earlier draft of this section implied is
a 400) plus a required **`subscriptionCreatePolicy`**; with **`AUTOMATIC`**, notification
eligibility is computed dynamically from user consents, so **no per-wearer Subscription
calls are ever needed** — an earlier draft of this runbook ended with a create-a-Subscription-per-enrolled-wearer
step, and that whole approach is obsolete. Provisioning is:

1. Read the generated secret from Secret Manager (`carditrack-<env>-webhook-secret` — the
   Terraform-owned value the receiver compares against).
2. Create the Subscriber — URL path uses the **project number, not the id** (id → bare 403),
   `endpointUri` is the **path-qualified** receiver URL (`https://<service>/webhooks/google-health`
   — the bare service root would 404 the probes), plus the secret and one `subscriberConfigs`
   entry listing the ingestion table's data types, policy `AUTOMATIC`.

> The endpoint-verification handshake is documented (webhooks guide, superseding this
> section's earlier "assumed GET" contract): on create/update Google sends **two POST
> probes** (User-Agent `Google-Health-API-Webhooks`, body `{"type": "verification"}`) — the
> one carrying the registered secret must be answered `200`/`201`, the unauthorized one
> `401`/`403`, else creation fails with `FAILED_PRECONDITION`. The receiver satisfies both
> sides and drops the probe body rather than forwarding it to the topic.

### Why not Terra?

Terra provides a unified wearable API but costs $499+/month minimum — too expensive at 10,000 users. CardiTrack integrates directly with the Google Health API, whose webhook subscriptions are free and already aggregate Fitbit, Pixel Watch, and connected third-party sources.

### Why Pub/Sub + 5-min batching?

10,000 devices at ~1 event/30s = ~333 events/s peak. Feeding each event directly to MedGemma would saturate the inference service. Batching per user over 5-minute windows reduces inference requests from ~333/s to a manageable ~33/s burst, significantly improving utilisation and cost — especially important while MedGemma runs as a single CPU instance.

### Token storage

Google-issued OAuth tokens for device connections are stored **encrypted (AES-256-GCM) in Cloud SQL** (`DeviceConnections` table) — the transactional system of record. The pipeline reads them via the existing repository layer; `CardiTrack.Worker` owns proactive token refresh. See [infrastructure.md](./infrastructure.md#storage-boundary).

---

## Prompt Structure

Each inference request covers a single user's 5-minute aggregated window.

> **Prefix caching does not currently happen, and cannot with this model.** The fixed-prefix
> construction described throughout this section is still how prompts are built — instructions
> first, byte-identical between calls, member data strictly after — but the serving-engine reuse it
> was designed to earn is not being realised, and no configuration change will earn it.
>
> Measured against dev on 2026-08-13, on a warm instance with the model resident and no other
> request in between, `llama.cpp` reported on every generation:
>
> ```
> checking checkpoint with [0, 492] against 0...
> forcing full prompt re-processing due to lack of cache data
>           (likely due to SWA or hybrid/recurrent memory)
> erased invalidated context checkpoint (n_swa = 1024)
> cached n_tokens = 0, memory_seq_rm [0, end)
> ```
>
> The cause is **sliding-window attention**: Gemma 3 declares
> `gemma3.attention.sliding_window = 1024`, and `llama.cpp` cannot restore a KV checkpoint under
> SWA, so it discards it and reprocesses from token zero. The machinery runs and works — it finds
> the common prefix by LCP similarity and stores the state — and then throws it away. It costs
> ~336 ms per request and ~508 MiB of held state for no benefit.
>
> Two consequences worth carrying: **prompt length is the only lever on inference latency** on this
> model, so trimming a prompt is worth what it looks like it is worth and nothing is waiting to
> make it cheaper; and the fixed-prefix discipline below should be kept anyway, because it costs
> nothing and pays off the day the model or the serving engine changes.

**System prompt** (fixed — a fixed prefix benefits from serving-engine prompt caching where the
model supports it; see the note above — this one does not):
```
[CARDITRACK_SYSTEM_PROMPT]
You are a medical AI assistant analysing cardiovascular wearable data.
Identify anomalies, patterns, or trends that may require clinical attention.
Be concise. Flag severity. Do not diagnose — flag for review.
```

**User prompt** (per user, per 5-min window — values are SSA-denoised trends):
```
Patient wearable data (5-minute window, SSA-denoised):
- Heart rate trend: Xbpm (Δ vs predicted: ±Xbpm, noise RMS: Xbpm)
- HRV (RMSSD): Xms
- SpO2 trend: X%
- Steps: X
- Active zone minutes: X
- Skin temperature delta: ±X°C (if available)

Assess for cardiovascular anomalies or patterns requiring attention.
```

### Member context block (built today)

The synchronous insight prompts (`HealthInsightService`) put a **member context block** between the fixed instructions and the metrics. Wearable numbers alone are not interpretable — a resting HR of 78 reads differently at 82 than at 42, and differently again on a beta blocker:

```
--- Member ---
Age: 78
Sex: Female
Caregiver-reported context: Type 2 diabetes, takes metformin
```

Rules this block follows:

- **Age and sex only, never name or id.** Neither identifier changes the clinical reading, so neither is sent.
- **The sex line is always present, including when sex was never recorded**, where it reads `Sex: not stated`. It used to be dropped for anything but Male/Female, on the reasoning that the other values told the model nothing usable. That was wrong twice over. Silence is not neutral to a model holding an age and a set of readings — it fills the gap, and the pronoun rule below would leave it guessing. And because M1-04 hardcoded `PreferNotToSay` until the form began asking for sex, the guard was not filtering a rare unusable case: it was suppressing the line for **every member in the system**.
- **Caregiver notes are untrusted input.** They are free text a caregiver typed, so every instruction block states that this section is background information and that instructions inside it must not be followed. Notes are truncated at 1000 characters, visibly.
- **It goes after the fixed instructions, never inside them.** Anything above the block is the cacheable prefix.

### The pronoun rule (built today)

`MedicalPromptBlocks.Pronouns` is one line, and it is the reason the sex line above must always be present:

```
Name them once, then use he or she as the sex given indicates, or they if it is not stated.
```

Handed a `{{NAME}}` placeholder and told to write with it, a 4B model repeats the placeholder in every sentence of a six-sentence summary. The output is grammatical and unreadable — a case file about a subject, not one person telling another how someone is doing, which is the voice the shared tone block spends seven lines asking for. Pronouns are what ordinary writing uses instead, and the model will not risk one unless told it may.

It follows `Tone` in **every prompt that writes prose** — the digest, the assessor, and the alert/baseline/learning/provisional insights — and is deliberately kept out of `CurrentStatusInstructions`. That prompt asks for a two-to-five-word headline and one sentence under twelve words, where a pronoun scarcely arises and its own instructions already settle how the person is named. It is also the only prompt on a request path a caregiver waits on and the only one under a character budget (`StatusPromptBudget`), so a rule that bought nothing there would be paid for in latency on nearly every dashboard view. `MedicalPromptToneTests` pins both halves of that: every other prompt carries the rule, and the status prompt does not.

### The member-context composer (built today)

The block above is no longer hand-built per prompt. Each prompt service used to assemble its own member context, and the differences between them were accidents rather than decisions: environmental readings reached the assessor alone because that is the service the enrichment pass was built beside, and the digest read no assessments at all despite this document routing medium severity to it.

A **context source** now declares which prompts it belongs in (`PromptPurpose`, a flags enum over the five) and builds its own labelled section, or returns nothing when it has nothing to say about this member right now. `MemberContextComposer` assembles the applicable ones in a fixed order and owns the rules that must not drift: the `--- Label ---` delimiter, the per-section length cap, and defusing any line in a body that tries to open a section of its own. Sources fetch their own data, which is what makes adding one a single class and a single registration in `AddMedicalAiServices` rather than an edit to every prompt service.

Four are registered:

| Source | Reaches | Carries |
|---|---|---|
| `DemographicsContextSource` | all five | Age, sex, and the caregiver note — **decrypted**. `MedicalNotes` is encrypted at rest, and until this source existed every prompt passed the stored column straight through, so the model read a `v1:…` ciphertext envelope where the conditions and medication were meant to be |
| `EnvironmentalContextSource` | all five | Temperature, described conditions, humidity and air quality from the member's last GPS-tagged session, consent-gated, with a per-prompt staleness rule (3 h for the assessor, up to 48 h for a trend analysis) |
| `MonitoringContextSource` | digest | Yellow-and-above assessments from the last 24 h and unresolved alerts — the medium-severity route this document has always specified |
| `QuestionnaireAnswersContextSource` | all but the hero status line | The family's three most recent answers to questions the digest asked |

A source with nothing to say produces no heading at all, which is a stronger guarantee than instructing the model not to mention it: on a calm member the words are not in the prompt to be echoed.

### Learning-phase and provisional prompts (built today)

Before a member has any `PatternBaseline` there is no normal to deviate from, so `CARDITRACK_LEARNING_PROMPT` replaces the trend prompt and asks the model to describe what has been observed so far and what is still missing — explicitly forbidding words like *elevated*, *low*, or *deviation*. The API reports this state as `isLearning` on the baseline-insight response, matching the dashboard's learning state so the two surfaces never disagree.

From about the first week, a **provisional** 7- or 14-day baseline exists before the 30-day one does. `CARDITRACK_PROVISIONAL_PROMPT` sits between the two framings: there is an early picture to compare against, so tentative comparisons are allowed ("so far", "appears", "early signs"), but nothing may be treated as an established pattern or cause for alarm on the strength of a short window. The response carries `isProvisional`, again mirroring the dashboard. Provisional baselines colour dashboards and soften insight phrasing only — **they never feed alert thresholds** (see [alerts.md](./execution/backend/api/alerts.md)).

---

## Family Sharing: When and How to Push Data

Family members are secondary consumers of CardiTrack data — they care about the *wearer's* safety, not their own metrics. The system must translate clinical-flavoured MedGemma output into plain-language, actionable summaries, and must respect the wearer's explicit consent at every step.

### Consent and access model

CardiTrack uses the **caregiver-centric** model defined in the API spec ([cardimembers.md](./execution/backend/api/cardimembers.md), [family.md](./execution/backend/api/family.md)):

- The account **Admin** (caregiver) creates the CardiMember profile and records the wearer's consent (per-metric: activity, heart rate, sleep) via the consent endpoint. Data types without recorded consent are never processed by the pipeline or shared.
- Family members join by **Admin invitation** with a role: `admin`, `staff`, or `viewer`.
- Per-member **family routing rules** control who is pushed what (e.g. a sibling receives `red` only) — stored in the **planned `AlertPreferences` table** (not yet implemented).

Role → visibility mapping:

| Role | What they see |
|------|---------------|
| `viewer` (or routing-restricted member) | Push notifications per routing rules; read-only dashboard |
| `staff` / `admin` | Alerts + daily digest + trend charts + settings |

Raw metric values (exact bpm, SpO2 %) are **hidden by default** in family-facing pushes and digests regardless of role; an Admin can expose them per member. This reduces anxiety-driven misinterpretation by non-clinical family members. A wearer with their own login retains binding controls: pause monitoring, add self-notes, and withdraw consent per metric.

---

### Trigger taxonomy: when to push

Each MedGemma response is parsed for a severity tag. The tag drives the push decision.

> **Severity mapping:** Critical/High/Medium/Low is the pipeline's **internal routing scale**. All user-facing surfaces (API, apps) use the product taxonomy: **Critical → `red`, High → `orange`, Medium → `yellow`, Low → `green` (health status; no alert emitted)**.

| Severity | MedGemma output signal | Family push? | Wearer push? | Cadence |
|----------|----------------------|:---:|:---:|---------|
| **Critical** | Sustained HR anomaly, SpO2 < 90%, HR > 150 at rest | ✅ Immediate | ✅ Immediate | Real-time (< 30 s) |
| **High** | HR trend deviation > 2 SD from 7-day baseline, HRV drop > 40% overnight | ✅ Immediate | ✅ Immediate | Within 5-min window |
| **Medium** | Mild trend deviation, elevated resting HR for 2+ consecutive windows | ❌ Held | ✅ In-app | **Built:** carried into the family summary. `MonitoringContextSource` reads the member's yellow-and-above assessments from the last 24 h, and their unresolved alerts, into `CARDITRACK_FAMILY_DIGEST_PROMPT`. Until this existed the tier routed nowhere — alerts fire at orange and above, and the digest read no assessments at all |
| **Low / Normal** | No anomaly detected | ❌ | ❌ | Silent; contributes to weekly trend |

> The LSTM's Δ anomaly score supplements MedGemma's severity judgement. If MedGemma rates a window "medium" but the anomaly score exceeds 3 SD on the trend, escalate to "high" before routing.

---

### Push channels and timing

```
MedGemma output (severity + plain-language summary)
  ↓
Severity router (Cloud Run)
  ├── Critical / High → FCM / APNs (immediate push — planned; no push infra yet)
  │                  → SMS fallback if app not installed (future; provider not selected)
  ├── Medium          → read into the next family summary by the digest job (below)
  └── Low / Normal    → trend_aggregates only — no push
```

**Daily digest** (08:00 local time, family + wearer):
- Plain-language overnight summary: sleep quality, HRV trend, any medium events from the prior 24 h (**built** — see `DigestGenerator`; the job runs half-hourly against the member's day in progress rather than once at 08:00)
- Generated by a second MedGemma call with a digest-specific system prompt (see below)
- Delivered as push notification with deep link to trend chart

**Weekly trend report** (Monday 09:00 local time, wearer only by default):
- 7-day cardiovascular trend: resting HR trajectory, HRV baseline shift, SpO2 stability
- Opt-in for family members at "Full dashboard" level

---

### MedGemma prompt variants by audience

The system prompt changes depending on whether the output is destined for a clinician review queue, the wearer, or a family member. The **user prompt stays identical** — only the framing of the response changes.

**Family member system prompt:**
```
[CARDITRACK_FAMILY_PROMPT]
You are summarising a loved one's heart health data for a non-medical family member.
Use plain, reassuring language. Avoid clinical jargon.
If there is nothing to worry about, say so clearly.
If there is a concern, describe it simply and recommend they check on their loved one.
Never diagnose. Never speculate about conditions. Do not include raw numbers unless severity is Critical.
```

> *(A wearer-audience digest prompt — `CARDITRACK_DIGEST_PROMPT` — used to sit here. Descoped 2026-08-10: wearers never log in, so there is no wearer to read it. Family is the only audience.)*
>
> Digest prompts are fixed per audience type, keeping them cacheable as fixed prefixes, the same as the real-time monitoring prompt.

---

### Inactivity and device-off detection

A family member's greatest fear is silence — not knowing whether no news is good news or a missed alert. **Built:** `InactivityDetectionWorker` in `CardiTrack.Worker` (rule-based, no MedGemma call — which is also why it lives in the Worker and not this pipeline, per CLAUDE.md) raises a **"device check"** alert when:

- No granular readings for a wearer for > 2 hours during waking hours (07:00–22:00 on the member's anchor clock, effectively from 09:00 so the whole silent window is inside waking hours and a charging watch never trips the first alert of the day)
- Silence means **no minute-grain readings**, deliberately not "no successful sync": a sync that completes and returns nothing is exactly the dead-battery case this alert exists to catch

The alert reads: *"No readings from the device since HH:mm. It may need charging, or a quick check that it is being worn."* — rule-based, keeping latency and cost at zero for the common no-data case.

> The detector emits the standard `Inactivity` alert (severity `yellow`) defined in [alerts.md](./execution/backend/api/alerts.md), so it appears in the alerts list and follows the normal acknowledgment lifecycle, with the same cooldown as the assessor's alerts: one unresolved `Inactivity` alert per member, resolved-to-re-arm. (The designed `device_disconnected` string taxonomy maps to the implemented `Inactivity` enum value; the separate `no_morning_activity` red variant — device syncing but no movement past typical wake time — is R1 statistical-engine territory.)

---

### Privacy guardrails

- Family members **never** receive the raw MedGemma inference output. A second, family-framed MedGemma call (or a template fill for low/normal windows) is always used.
- All AI-result rows are keyed by `wearer_user_id`. Family member reads are scoped by the `UserCardiMembers` relationship record in Cloud SQL — the query layer enforces this; there is no client-side filtering.
- Access is revocable at any time: an Admin removes the family member (or the wearer withdraws consent per metric); the relationship record is deleted and all future pushes for that pair stop immediately.
- Family-facing digests **do not include skin temperature** — this is too intimate a signal for a non-clinical audience and can cause disproportionate alarm.

---

## Trend Interpretation (formerly Predictive Monitoring)

Forward-looking awareness is CardiTrack's core market differentiator — every competitor reacts to emergencies; CardiTrack notices trajectories early. **Redesigned 2026-08-10:** the per-user LSTM risk model, its calibrated 0–100 risk scores, and the training/lifecycle machinery below it are dropped. In their place, the same early-warning value comes from three auditable layers:

1. **Deterministic trend features** — moving averages, slopes, and deviations computed in .NET from the multi-horizon rollups and R1 baselines (e.g. "resting HR up 6 bpm over 4 days against the 30-day baseline"). Code computes every number; nothing is estimated by a model.
2. **Pinned reference ranges** — a curated, versioned table of clinical norms (resting HR by age/sex, sleep-duration ranges, activity guidelines) sourced from named standards and injected into the prompt. The model never recalls benchmarks from its training data, so the yardstick behind every narrative is reviewable.
3. **MedGemma interpretation** — reads the computed features against the member's own history and the pinned ranges, and writes the family-facing trend narrative ("this trajectory is worth watching") that feeds digests and insights.

### What is watched

The signal patterns worth narrating are unchanged — they are simply *computed as rules* now rather than predicted as scores:

| Pattern | Deterministic signal | Framing to family |
|---------|---------------------|-------------------|
| **Possible illness onset** | Rising resting HR trend + declining HRV vs. baseline | "May be coming down with something — worth a check-in" |
| **Fatigue / overexertion** | Active zone minutes > personal 7-day average × 1.5 | "A lighter day could help" |
| **Poor sleep pattern** | Elevated evening HR, late activity, short prior nights | "A settled evening might help tonight's rest" |
| **Cardiac trend** | 3+ day resting HR rise > 5 bpm or HRV decline > 30% from 30-day baseline | "A trend the family should keep an eye on" |

> **What is never produced:** numeric risk scores or probabilities (an LLM cannot honestly calibrate them, and the dropped LSTM was the only component that could have tried), specific diagnoses, medication interactions, or acute cardiac event predictions. Outputs are qualitative trend observations, not clinical predictions.

---

### Trend interpretation pipeline (design)

```
Cloud SQL (MetricRollupsHourly + horizon views + PatternBaselines)
  ↓
Daily trend-feature computation (TrendInterpreter Cloud Run job, .NET — no ML)
  — Computes: resting HR 7d MA + slope, sleep 7d MA, active minutes 7d MA,
              deviations vs. 30/60/90-day baselines, day-of-week seasonality
  ↓
Cold start check
  ├── < 30 days data → no trend narrative (learning mode, as insights today)
  └── ≥ 30 days data → interpretation
  ↓
Pinned reference-range table (versioned in the codebase, injected into the prompt)
  ↓
MedGemma (CARDITRACK_TREND_PROMPT) — interprets computed features
  — Generates the plain-language trend narrative
  ↓
Routing: narrative feeds the family digest and the insights API
  (push dispatch joins when FCM/APNs lands from its workstream)
```

---

### False positive management

False positives are CardiTrack's primary churn risk (market target: <5% FP rate vs industry 20–30%). Two controls survive the LSTM's departure unchanged, and one dies with it:

**1. Consecutive signal requirement** — a trend pattern must hold on 2 consecutive daily computations before it is narrated as a concern. A single-day spike is logged but not surfaced.

**2. User-adjustable sensitivity** — Admins/Staff can set per-member sensitivity to Low / Medium / High (see [alerts.md](./execution/backend/api/alerts.md) alert-preferences), shifting the rule thresholds. Sensitivity will be stored in the planned `AlertPreferences` table in Cloud SQL (not yet implemented).

*(The former confidence gate is gone — it gated model confidence, and there is no model to be confident.)*

---

### MedGemma prompt variant: trend narrative

A separate system prompt ensures trend output is framed as forward-looking guidance, not a current-state alarm.

**Trend system prompt (design):**
```
[CARDITRACK_TREND_PROMPT]
You are summarising multi-day health trends for a family caregiver app.
You have been given computed trend features and the clinical reference ranges to read them against.
Use only the numbers and ranges provided; never supply your own reference values.
Write a short, plain-language trend note (2–3 sentences max).
Frame trajectories as possibilities, not certainties: "may", "could", "worth watching".
If everything is settled, lead with reassurance.
If one trend is drifting, mention it gently and suggest one practical action.
Never diagnose. Never alarm.
```

**Example output for a fatigue-pattern day:**
> "Based on recent activity levels, [Name] may feel more tired than usual today — a lighter day could help. Heart rate and sleep patterns look broadly stable. Nothing urgent, but a check-in this afternoon might be welcome."

**Example output for a settled week:**
> "[Name]'s health patterns look settled. Resting heart rate and sleep quality have been consistent this week — a good sign."

> Fixed-prefix and cacheable, the same as every other prompt in the registry.

---

### Updated prompt structure summary

| Prompt | Cadence | Audience | Purpose |
|--------|---------|----------|---------|
| `CARDITRACK_SYSTEM_PROMPT` | Every 5 min | Internal (clinical review queue) | Real-time anomaly flagging — **built today as `CARDITRACK_REALTIME_ASSESSMENT_PROMPT`** (`RealtimeAssessmentService`): denoised trend + deviation yardsticks in, 1–3 caregiver-actionable sentences plus a strict closing `Severity:` line out |
| `CARDITRACK_LEARNING_PROMPT` | On request, before any baseline | Caregiver | What has been observed so far, before any baseline exists — **built today** |
| `CARDITRACK_PROVISIONAL_PROMPT` | On request, while only a 7/14-day baseline exists | Caregiver | Early impressions against a provisional baseline, phrased tentatively — **built today** |
| `CARDITRACK_FAMILY_PROMPT` | On high/critical events | Family members | Plain-language alert |
| `CARDITRACK_FAMILY_DIGEST_PROMPT` | Whenever the member's readings have moved past their last summary, or their alert state changes (half-hourly job) | Family members | Headline, summary of the local day so far, three supportive suggestions, and optionally one question for the family — **built today** (append-only store with history + API read; push pending) |
| ~~`CARDITRACK_DIGEST_PROMPT`~~ | — | ~~Wearer~~ | **Descoped 2026-08-10** — wearers never log in; self-monitoring is not the product |
| `CARDITRACK_TREND_PROMPT` | Daily (design) | Family members | Trend narrative over computed features + pinned reference ranges — replaces `CARDITRACK_PREDICT_PROMPT`, which died with the LSTM's risk scores |

---

## Cost Estimates

| Component | Estimated Cost | Notes |
|-----------|---------------|-------|
| Cloud Run — MedGemma (4 vCPU / 16 Gi, CPU always-allocated, 1 instance) | ~£150–175/mo when kept warm | Largest AI line item. Scale-to-zero keeps *idle* cost near zero but not *wake* cost: a cold start bills the full allocation for the ~150s the startup probe allows, so spend tracks scheduler cadence, not member count. The Aug 2026 dev overrun (~£13/day) was this — a `*/5` assessor paying up to 12 cold starts per assessment produced |
| Cloud Run — pipeline services/jobs (CPU) | Near-zero at this scale | SSA-LSTM pre-processor + predictive batch |
| Cloud Pub/Sub | ~£5–10/mo | Real-time ingestion buffer at ~333 events/s peak |
| Cloud SQL headroom (JSONB result tables) | Within existing instance | No separate data plane to pay for |
| GCS (per-user model store) | ~£0.50/mo | ~500 MB for 10,000 per-user LSTM models |
| Gemini 2.0 Flash API | Usage-based, small | General-provider calls (chat/reports) |
| Google Health API | Free | Restricted scopes — production access requires Google's privacy & security review |
| Google Maps Platform (Weather + Air Quality APIs) | Usage-based, low volume | Fires per GPS-tagged exercise session, per consented member — orders of magnitude below the heart-rate path's request volume |
| Terra API | Not used — $499+/mo | |

---

## Important Caveats

- MedGemma is **not clinical-grade** out of the box. Outputs must be validated before use in any production health context.
- MedGemma is **not optimised for multi-turn conversation**. Treat each inference request as stateless.
- All patient data processed through MedGemma must comply with applicable health data regulations (HIPAA, GDPR, etc.).
- All Google Health API scopes are classified **Restricted** — production (verified) access requires passing Google's privacy & security review; before verification, only enrolled test users can connect devices.
- The system prompt is identical across all users, making it an ideal candidate for serving-engine prefix caching — ensure it is never personalised per user to preserve this benefit.
- The `googlehealth.location.readonly` scope the environmental-enrichment job needs has **not yet been requested from Google** — it is a new Restricted scope on top of the ones already granted, and requesting it re-opens the privacy & security review scope, not just an app update. Until it is granted, the `enrich` job's exercise fetch returns nothing for every connection (no connection carries the scope), which is a safe, silent no-op rather than a failure.
- Environmental-context enrichment is the platform's first geolocation data of any kind. It ships **consent-gated off by default** (`CardiMember.EnvironmentalContextConsentGranted`) precisely because the platform's broader per-metric consent architecture is still design-only (`docs/technical/data_protection_architecture.md` §8) — see that document and the DPIA for the compliance conditions this feature was built under.

---

*Version 2.1 — Last Updated: August 12, 2026*
