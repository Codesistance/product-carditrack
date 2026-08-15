# LLM Design — CardiTrack

> **STATUS — read this first**
>
> - **Built today:** MedGemma (Ollama-served `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` on Cloud Run, enabled in dev, scale-to-zero) as the **Medical** AI provider and **Gemini 2.0 Flash** as the **General** provider, consumed by `GenerativeAiService`, `MedicalAiService`, `HealthInsightService`, and `ReportGenerationService` and surfaced through the API's **chat, insights, reports, and caregiver-ask** endpoints (`ChatController`, `InsightsController` including `POST .../ask`, `ReportsController`). Insight prompts carry a **member context block** (age, sex, caregiver notes — never name or id) and switch by baseline state: a **learning-phase variant** while no baseline exists at all, a **provisional variant** while only a short-window (7/14-day) baseline does, and the full trend prompt once the 30-day baseline lands. The **family summary** is the first *background* LLM process: a Cloud Run job (`carditrack-<env>-pipeline-jobs`, half-hourly Cloud Scheduler, dev only) recomputes a plain-language summary per member whenever their readings have moved since the last one and their previous summary is at least an hour old (2 hours early in their day; overnight the floor does not lift) — **waived** when samples indicate a problem (yellow+ assessment or SSA jump), daily readings diverge from the 30-day baseline, they jumped from yesterday, or an alert was raised or resolved — via MedGemma, and appends it — every generation is kept — for `GET /api/v1/insights/members/{id}/digest` — wired through `AddMedicalAiServices`, so the job carries no public-provider key at all. Ingestion is **10-minute polling** of the Google Health API by `WearableSyncWorker` in `CardiTrack.Worker`.
> - **Real-time path (built, dev):** the webhook receiver and 5-minute aggregator are live with the **Subscriber registered against Google (2026-08-10)** — notifications flow end to end — and the **real-time assessment** now runs end to end off the granular store: a 5-minute Cloud Run job (`carditrack-<env>-pipeline-jobs-assessor`, offset two minutes from the aggregator) takes each member's latest hour of heart rate, decomposes it with **SSA** (BK lag-covariance + Math.NET Numerics symmetric EVD, `SsaDecomposition` in Infrastructure behind `ISsaDecomposition`), and **asks MedGemma only when the SSA deviation score is a jump** (≥3 typical jitters from trend). Ordinary windows are scored and skipped — not stored — so a later tick can still consult the model if the hour jumps. A concerning window is stored in the partitioned `RealtimeAssessments` table (90-day retention by partition drop); red/orange verdicts create `Alert` rows — one unresolved heart-rate alert at a time — then **POST the alert id to the API's internal enqueue endpoint** so caregivers are pushed through the same notification engine the Worker uses, then **re-run digest generation** on the same execution so a concerning window rewrites the family summary instead of waiting for the next half-hourly digest schedule.
> - **Environmental-context enrichment (built in code and schema, NOT provisioned — it never runs today):** for GPS-equipped wearables, a fourth job mode (`--job enrich`, which would run as `carditrack-<env>-pipeline-jobs-enricher`) is in the pipeline image, but **no Cloud Run job or Cloud Scheduler trigger exists for it in any environment**. When provisioned, it looks up ambient temperature and air quality (Google Maps Platform Weather + Air Quality APIs) for a member's GPS-tagged exercise sessions and folds the derived values into the real-time assessment prompt. Gated on a new per-member `CardiMember.EnvironmentalContextConsentGranted` flag — default `false`, the sole candidate filter — and on a new Restricted OAuth scope (`googlehealth.location.readonly`) not yet requested from Google. **Raw GPS coordinates are never persisted**: the enrich job reads a session's coordinates only long enough to call the environmental APIs, and only the resulting temperature/AQI values are stored, in the partitioned `EnvironmentalReadings` table (90-day retention). Noise/sound-level context was scoped out — no location-queryable data source exists at production grade.
> - **Design decisions, 2026-08-10:** the **LSTM is dropped**, not parked. Personalization comes through the context window instead: deterministic .NET computes every number (SSA, baselines, multi-horizon rollups), MedGemma interprets them, and clinical reference ranges are **pinned in a curated table injected into the prompt** — never recalled from model weights, so every assessment's yardstick is auditable. With it go the per-user model files, the Python training job, ONNX, and the calibrated numeric risk scores (0–100 fall-risk etc.) — the predictive path becomes the **trend interpretation** design below. **Wearer-audience features are permanently descoped**: wearers never log in; self-monitoring is not the product.
> - **Still design-only:** trend interpretation (waits on the R1 statistical engine's baselines), and the digest's own push (a new summary does not page). Push delivery itself is **built** — the notification engine's FCM HTTP v1 relay with APNs passthrough, escalation ladder and quiet hours — and the assessor's red/orange path now POSTs to `/api/v1/internal/notifications/enqueue`. Medium (yellow) stays summary-only.

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
| **Cloud Run services/jobs + Cloud Scheduler** | All pipeline logic — webhook receiver, aggregation, SSA pre-processing, assessment, environmental enrichment, trend interpretation, digest, push dispatch | **Built (dev):** digest + aggregator + assessor jobs (`carditrack-<env>-pipeline-jobs[-aggregator|-assessor]`) and the webhook receiver service, gated on their enable flags. The enricher exists only as a `--job enrich` mode in the image — **no Cloud Run job or scheduler is provisioned for it**. Trend interpretation remains target design; the assessor's severity→push wiring is built (internal enqueue) |
| **Cloud Pub/Sub** (`carditrack-<env>-realtime`) | Wearable raw event stream buffer | Topic + pull subscription provisioned in **dev and prod** (`enable_pubsub`); the receiver publishes and the aggregator drains it in dev, carrying **live registered traffic** since 2026-08-10 |
| **Cloud SQL PostgreSQL (existing instance)** | OAuth tokens (encrypted AES-256-GCM in `DeviceConnections`), user profiles, sensitivity settings, family relationships — the transactional system of record (see [infrastructure.md](./infrastructure.md#storage-boundary)); plus typed partitioned tables for AI results (below) | Built — core schema plus `DigestEntries`, `RealtimeAssessments`, and `EnvironmentalReadings` |
| **FCM / APNs** | Push routing for alerts and digests | **Built** — FCM HTTP v1 relay with APNs passthrough (escalation ladder, quiet hours) shipped via the notification engine; the assessor POSTs orange/red alert ids to the internal enqueue endpoint. Digest push is what remains |
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
| `RealtimeAssessor` | Cloud Scheduler | Every 5 min, at :02/:07/… (offset two minutes from the aggregator) | **Built (dev):** `carditrack-<env>-pipeline-jobs-assessor` — for each member with fresh data, SSA over the latest 60-minute heart-rate window (≥45 covered minutes; window keyed by its start, so an unmoved window costs no inference). **MedGemma (`CARDITRACK_REALTIME_ASSESSMENT_PROMPT`) runs only when the SSA deviation score is ≥3** — ordinary windows are not stored, so a later tick can still consult the model if the hour jumps. A jump is written to the partitioned `RealtimeAssessments` table. Works entirely off the granular store, so it functions on polling alone — webhook registration only makes it fresher. Reads a recent `EnvironmentalReading` (below) through the shared member-context composer, which now carries it into every medical prompt rather than this one alone — the assessor keeps its own three-hour staleness rule. **After the assessment pass it re-runs digest generation** so a yellow+/jump window rewrites the family summary on the same execution (MedGemma is already warm) rather than waiting until `:00`/`:30` |
| `EnvironmentalEnricher` | Cloud Scheduler | Every 15–30 min (offset from the other jobs) | **Built in code and schema, NOT provisioned — no Cloud Run job or scheduler exists, so it never runs today.** When provisioned as `carditrack-<env>-pipeline-jobs-enricher` — for members with `CardiMember.EnvironmentalContextConsentGranted = true` (the sole candidate filter) and a connection granted the `googlehealth.location.readonly` scope, fetches GPS-tagged exercise sessions (Google Health API `exercise` data type + `exportExerciseTcx`), looks up ambient temperature, described conditions, humidity and air quality for each new session's coordinate (Google Maps Platform Weather + Air Quality APIs — conditions and humidity ride along on the current-conditions response the temperature lookup already makes, so they cost no extra call), and stores only the derived values in the partitioned `EnvironmentalReadings` table — **the raw coordinate is never persisted**, discarded immediately after the environmental lookup. Runs as its own job rather than inline in the aggregator: exercise sessions are sparse and don't need 5-minute freshness, and an external vendor call does not belong in the hot sync loop 10,000 devices go through every few minutes. The Google Maps Platform key and the exercise/GPS device-client methods are registered nowhere else in the process (`EnvironmentalServiceExtensions`), so no other host or job path can reach them |
| `SeverityRouter` | On new result row | On write | **Built (dev), inline in the assessor rather than a separate component:** the model's closing `Severity:` line is parsed strictly (critical/high/medium/low → red/orange/yellow/green; an unparseable answer is stored but routes nowhere — the model cannot page a family by mumbling), and red/orange verdicts create `Alert` rows with a one-unresolved-heart-rate-alert-at-a-time cooldown, then POST the alert id to `POST /api/v1/internal/notifications/enqueue` (Google OIDC, pipeline service account). The API's `DispatchService` owns recipient resolution, quiet hours, dedup and escalation — the pipeline holds no send stack. A failed enqueue is logged and does not roll back the alert. Medium (yellow) is stored for the digest and is not pushed |
| `TrendInterpreter` | Cloud Scheduler | Daily (design) | Replaces the former LSTM predictive path (dropped 2026-08-10): reads the member's multi-horizon rollups + R1 baselines, computes trend features deterministically (moving averages, slopes, deviations — .NET, no ML), injects the **pinned clinical reference-range table**, and asks MedGemma for a family-facing trend narrative feeding digests/insights. No risk scores, no per-user models. The R1 statistical engine it reads from is built (`StatisticalAlertWorker` in the Worker); what remains is this interpretation job and its pinned reference-range table |
| `DigestGenerator` | Cloud Scheduler | **Built (dev):** half-hourly scheduler, plus a second pass at the end of each assessor run (every 5 min). Regenerates for **whichever members' readings have moved since their last summary** — members whose data has not changed, and members summarised within the last 20 minutes, are skipped before any model call. The two gates decouple the cadence from the *inference* bill: the jobs can run often enough to catch a quiet member up quickly without regenerating a continuously-uploading one on every pass. They do not make the cadence cost-free, though — where MedGemma scales to zero a pass that finds any work at all pays a cold start at the full CPU allocation, and where it is kept warm (dev today, `medgemma_min_instances`) the cadence drives inference volume against an instance bill that runs regardless. **The floor is waived** when an alert was raised or resolved, when the latest real-time window is yellow-or-above or an SSA jump (≥3 typical jitters from trend), or when new daily readings diverge from the 30-day baseline or jumped more than 30% from yesterday (SpO₂: 3 points). Those are the changes that rewrite what the summary should say; making a caregiver wait out the floor to read them — including a medium observation that has not (yet) become an alert — would be the floor working against its own purpose. Ordinary new readings still ride the cycle | Summarises the member's local day in progress (their anchor timezone is the earliest-linked caregiver's `User.TimeZoneId`) → calls MedGemma (`CARDITRACK_FAMILY_DIGEST_PROMPT`, returning a short `headline`, the summary text, one `suggestion` and an `urgency`) → **appends** to the partitioned `DigestEntries` table, whose key carries `GeneratedAtUtc` so every recomputation is kept as history (**7-month** retention by partition drop); read via `GET /api/v1/insights/members/{id}/digest` (current) and `GET /api/v1/insights/members/{id}/digests` (history). The same call may also propose **one short question to the family** when the readings would be clearer for an answer; it is stored as a `MemberQuestionnaire` (see `docs/execution/backend/api/questionnaires.md`) under noise gates — one open question per member, seven days since the last ask (standing/`Permanent` questions skip the floor; a gap-backed ask from an unresolved alert or Yellow+ observation may fire with a 12-hour ceiling; dismissed and answered-permanent never re-asked). Push infrastructure is built (FCM HTTP v1 relay with APNs passthrough); the digest's own push wiring is what remains. The family audience is the only audience — wearers never log in |
| `InactivityDetector` | ~~Cloud Scheduler~~ Worker cron | Every 15 min | **Built:** `InactivityDetectionWorker` in `CardiTrack.Worker` — this table originally drew it beside the pipeline, but it makes no AI call, and non-AI background jobs are Worker-exclusive per CLAUDE.md, so placement follows the rule, not the diagram. Silence means **no granular readings** (a sync that returns nothing is exactly the dead-battery case), measured on the member's anchor clock: >2 h without a minute during waking hours (07:00–22:00 local, effectively from 09:00 so the charger never trips the first alert of the day) raises one yellow `Inactivity` alert, suppressed until resolved |

> **Runtime note:** all pipeline components run on **.NET**, matching the rest of the platform. Numeric stages are in-process: SSA eigen-decomposition is Math.NET Numerics (MIT, Infrastructure; see [mathnet_numerics.md](./technical/mathnet_numerics.md)), baselines and R1 alert rules remain hand-rolled Application arithmetic. With the LSTM dropped (2026-08-10) there is no ONNX runtime, no TensorFlow, and no Python anywhere in the pipeline — the former `ModelRetrainer` training job is gone with it.
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
| Used by | Health insights and caregiver ask (`HealthInsightService`) | Reports (`ReportGenerationService`), chat (`ChatController`) |
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

> **GPU scaling option — no longer future:** CPU latency became the bottleneck on 2026-08-17, when p50 inference reached ~124s and Cloud Run began refusing ~14% of calls with 429 because `max_instances = 1` leaves nowhere to put an overlapping request. The options, their measured cost basis, and the region constraint (Cloud Run's managed GPU is not offered in `europe-west2`, though L4 is available there on Compute Engine and GKE) are worked through in [medgemma_serving_architecture.md](./technical/medgemma_serving_architecture.md), which carries the open decisions.
>
> These are **two separate moves**, and the ADR argues for taking them in this order:
>
> 1. **Compute — CPU to GPU, keeping Ollama.** The container and the GGUF are unchanged; only the hardware underneath differs. No HuggingFace weights access is needed, so nothing gates it but the region and cost decisions in the ADR. This is where the order-of-magnitude latency win lives.
> 2. **Serving engine — Ollama to vLLM, later.** `--enable-prefix-caching` is the only way to stop re-reading the fixed instruction block on every call, which llama.cpp cannot avoid under Gemma 3's sliding-window attention. Sampled traffic is ~513 input tokens to ~18 output, so that re-read is close to the whole cost — the case is stronger than this note originally assumed. It is second because it **does** require HuggingFace weights access (Health AI Developer Foundations terms), and because vLLM is a GPU-only proposition that would not improve anything on today's CPU. The target shape is unchanged from the original sketch: the same model served by vLLM on a single NVIDIA T4 (16 GB, float16 — the 4B model fits with KV-cache headroom), autoscaling on HTTP concurrency. Provisioning would be added to the existing Terraform — no imperative scripts.

---

### AI results: PostgreSQL tables (built as typed + partitioned; the original JSONB sketch below is kept for lineage)

AI outputs are derived data in the **existing Cloud SQL instance** — regenerable, never authoritative. The built tables are keyed by **`CardiMemberId`** with **typed columns, day-partitioned** — the original sketch's `wearer_user_id` keys and JSONB payload columns were both dropped (there is no wearer user; typed columns beat JSONB key-bloat at volume, per the granular-storage ADR).

| Table (original sketch) | Key columns (sketch) | JSONB payload (sketch) | As built / retention |
|-------|-------------|---------------|-----------|
| `realtime_results` | `wearer_user_id`, `window_start`, `severity` | `medgemma_output`, `anomaly_scores` | **Built as the typed, day-partitioned `RealtimeAssessments` table** (`CardiMemberId`, `WindowStartUtc` PK; SSA features, model output and routed severity as columns — typed rather than JSONB, per the granular-storage ADR) — 90 days by partition drop |
| `prediction_cards` | ~~`wearer_user_id`, `date`~~ | ~~`risk_scores`, `confidences`, `medgemma_output`~~ | **Descoped 2026-08-10** with the LSTM — trend interpretation writes narrative into digests/insights, not risk-score rows |
| `trend_aggregates` | `wearer_user_id`, `date` | `resting_hr_7d_ma`, `hrv_7d_ma`, `sleep_score_7d_ma` | **Built as the typed `MetricRollupsHourly` table + week/month views** (keyed by `CardiMemberId`) — 13 months by partition drop |
| `digest_log` | `wearer_user_id`, `date`, `audience` | `digest_text` | **Built as the typed, day-partitioned `DigestEntries` table** (`CardiMemberId`, `LocalDate`, `Audience`, `GeneratedAtUtc` PK; family, daybook and weekbook audiences today) — 7 months by partition drop |
| *(net-new)* | — | — | **Built as the typed, day-partitioned `EnvironmentalReadings` table** (`CardiMemberId`, `SessionStartUtc` PK; `TemperatureCelsius`, `AirQualityIndex`, `AirQualityCategory` as columns — no latitude/longitude column exists on this table, structurally) — 90 days by partition drop |

Row expiry is a **partition drop performed hourly by `PartitionMaintenanceWorker`** in `CardiTrack.Worker` (digests and CardiJournal entries 7 months, assessments 90 days, environmental readings 90 days) — PostgreSQL has no document TTL, and no other retention job exists.

---

### Deployment

Everything is provisioned by the existing Terraform (`infrastructure/` — see the [operator guide](../infrastructure/README.md)):

- **MedGemma service** — `deployments/cloud_run.tf` (gated on `medgemma_image`); image built from `src/Infrastructure/MedGemma/Dockerfile`
- **Pub/Sub topic + subscription** — `deployments/pubsub.tf` (gated on `enable_pubsub`; **enabled in both dev and prod**)
- **Secrets** — `deployments/secret_manager.tf` (`gemini-api-key`, `medgemma-service-url`, `webhook-secret`)
- **Pipeline components** — already in the same Terraform: the webhook receiver service, the digest/aggregator/assessor Cloud Run jobs with their Cloud Scheduler triggers, and a dedicated `pipeline_scheduler` service account that invokes them (`deployments/cloud_run.tf`, gated on `enable_pipeline_jobs`/`enable_webhook_receiver`). The **enricher is the only unprovisioned component** — its `--job enrich` mode ships in the image with no job or scheduler behind it

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
│  → Trend narrative → Family digest                          │
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
SSA pre-processor — denoises signal, extracts trend features
  ↓
MedGemma service (Ollama on Cloud Run, internal VPC)
  ↓
Cloud SQL typed partitioned tables (results store)
```

---

## SSA Pre-Processing Layer

Before each window is sent to MedGemma, raw wearable time-series data passes through Singular Spectrum Analysis. SSA decomposes each metric into **Trend**, **Oscillation**, and **Noise** components; MedGemma receives the denoised trend values rather than raw averages, improving anomaly sensitivity.

> **Built:** `SsaDecomposition` in `CardiTrack.Infrastructure` (port `ISsaDecomposition` in Application), BK lag-covariance + Math.NET Numerics symmetric EVD (`SsaParameters.Engine = "MathNet.Numerics.Evd"`; window 30, trend + 2 oscillation components, noise as the residual). The previous cyclic-Jacobi solver was replaced 2026-08-14 — same algebra, named library. The deviation check compares the **actual latest reading against the SSA trend, in units of the noise RMS** — the member's own jitter as the yardstick; the assessor's stored `HrDeviationScore` is exactly this. A change of engine is an Art. 22 V4 trigger ([art22_alerting_analysis.md](./compliance/art22_alerting_analysis.md); [mathnet_numerics.md](./technical/mathnet_numerics.md)).
>
> **The LSTM forecast that used to sit beside SSA was dropped 2026-08-10.** Personalization beyond the SSA window comes from the R1 baselines and rollups fed through the prompt (see Trend Interpretation), not from a trained per-member forecaster — no training pipeline, no per-member model files, no calibrated risk scores.

### Role in the pipeline

| Stage | Input | Output |
|-------|-------|--------|
| SSA decomposition | Raw intraday time-series | Trend + Oscillation components per metric |
| Deviation check | Actual latest reading vs. SSA trend | Deviation score in noise-RMS units |
| MedGemma prompt | Denoised trend + deviation yardsticks | Cardiovascular assessment + severity verdict |

### Google Health API Data Type → SSA Input Mapping

The Google Health API consolidates the legacy per-endpoint surface into **data types** queried via `list` (intraday/granular) and `rollUp`/`dailyRollUp` (summaries) at `https://health.googleapis.com`.

Methods follow the v4 REST shape: `GET /v4/users/me/dataTypes/{type}/dataPoints` (list) and `POST .../dataPoints:dailyRollUp` (daily summary; heart-rate/active-minutes/total-calories rollups max 14-day range, others 90 days).

A `dailyRollUp` body takes `range` as a **`CivilTimeInterval`** — closed-open, with `start`/`end` each a **`CivilDateTime`**, meaning the calendar date nests under `date` (`{"start": {"date": {"year": …, "month": …, "day": …}}}`) and `time` is omitted to mean midnight. A bare `{year, month, day}` at `range.start` is rejected with `INVALID_ARGUMENT` field violations — the shape the API client (`FitbitApiClient`, since renamed `GoogleHealthApiClient`) sent until it was corrected against the live API.

`list` takes its window as an AIP-160 `filter` query parameter instead, and **each filterable field admits exactly one literal format**: physical-time fields (`{type}.interval.start_time`, `sleep.interval.end_time`) require an RFC-3339 instant (`"2026-08-05T00:00:00Z"`), while their civil-time siblings (`{type}.interval.civil_start_time`, `sleep.interval.civil_end_time`) require an ISO 8601 civil time in one of exactly two forms — date-only `2026-08-05` or date-time `2026-08-05T00:00:00`, never with a `Z` or offset suffix (Google's reference writes this as `yyyy-MM-dd[THH:mm:ss]`, where the brackets denote an optional segment, not literal characters). Mixing the two is not coerced — a bare date against `sleep.interval.end_time` returns a 400 with reason `INVALID_DATA_POINT_FILTER` and `detailedReasons: INVALID_DATA_POINT_FILTER_TIMESTAMP_FORMAT`, which is exactly what the client sent until it was corrected against the live API. Only `>=` and `<` are supported, and sleep is filterable on **end time only** (sessions are attributed to the day they ended on).

Prefer the **civil** variants throughout. Physical-instant filtering buckets by UTC day while every `dailyRollUp` above buckets by the wearer's local day, so for a wearer west of Greenwich a late-evening sleep session would land in the next day's snapshot while the same night's steps stayed in the current one — a silent per-timezone misalignment in the SSA input series, not an error. A unit test pins the emitted sleep filter string.

| Metric | Data type / method | Sampling Rate | SSA Input |
|--------|--------------------|----|-----------|
| Heart Rate (intraday) | `heart-rate` — `list` | 1-min intervals | Primary time-series for SSA decomposition |
| Resting Heart Rate | `daily-resting-heart-rate` — `list` | Daily scalar | Baseline anchor for HR trend |
| HRV (RMSSD) | `daily-heart-rate-variability` — `list` (granular: `heart-rate-variability` — `list`) | Daily scalar | Secondary series |
| SpO2 (intraday) | `oxygen-saturation` — `list` | ~5-min intervals | Upsample to 1-min via forward-fill before SSA |
| Steps (intraday) | `steps` — `list` | 1-min intervals | Used as activity context feature alongside HR |
| Active Zone Minutes | `active-zone-minutes` — `list` | 1-min intervals | Activity-context feature alongside HR |
| Skin Temperature | skin-temperature data type — `dailyRollUp` | Daily scalar (nightly) | Early-warning feature; include when available |
| Sleep Stages | `sleep` — `list` (session-shaped) | Daily summary | Context feature for next-day recovery model |

> **Not every data type supports every method.** A type's *record type* decides: Interval and Sample types (`steps`, `distance`, `active-minutes`, `total-calories`, `floors`, `sedentary-period`, `heart-rate`, `oxygen-saturation`) roll up; **Daily** types (`daily-resting-heart-rate`, `daily-heart-rate-variability`, `daily-oxygen-saturation`, `daily-vo2-max`, `daily-respiratory-rate`, `daily-sleep-temperature-derivations`) are already one point per day and support only `list`/`reconcile`; Session types (`sleep`, `electrocardiogram`, `irregular-rhythm-notification`) support `list`/`get`. A rollup can also be the wrong method for a rollable type: `dailyRollUp`'s union has no `oxygenSaturation` member, so SpO2 min/max exist only in the sample series and `GoogleHealthApiClient` lists it rather than rolling it up. Asking a Daily type for a rollup returns 400 `INVALID_ARGUMENT` with reason `INVALID_PARENT_DATA_TYPE_COLLECTION` — the client rolled up a `resting-heart-rate` that is neither a real type ID nor rollable, which failed every wearable sync until it was corrected. A Daily type's window is a `filter` on its own `date` field, using the same ISO literal and `>=`/`<` grammar as the civil-time fields above.
>
> **`filter` member paths are snake_case**, and they name the *data type*, not the response's union member: the documented patterns are `{daily_summary_data_type}.date`, `{interval_data_type}.interval.civil_start_time`, `{sample_data_type}.sample_time.civil_time` and (sleep-specific) `sleep.interval.civil_end_time`. So it is `daily_resting_heart_rate.date >= "2026-08-05"` — the camelCase `dailyRestingHeartRate.date` that keys the *response* is rejected with `INVALID_DATA_POINT_FILTER` / `INVALID_DATA_POINT_FILTER_DATA_TYPE_RESTRICTION` ("does not match any data type"), which failed every wearable sync until it was corrected. Single-word types like `sleep` spell the same either way, so they prove nothing about the convention.
>
> Rollup responses carry a union value per data type, and its fields are named **`{field}{Aggregation}` in camelCase** — `steps.countSum`, `heartRate.beatsPerMinuteMin/Max/Avg`, `distance.millimetersSum`, `totalCalories.kcalSum`, `floors.countSum`. Units are the schema's own — distance is **millimetres**, not metres. `active-minutes` is the exception with no scalar total, returning `activeMinutesRollupByActivityLevel[]`, of which CardiTrack sums the `MODERATE` and `VIGOROUS` levels to match Fitbit's classic figure. Sleep carries `summary.stagesSummary[]` keyed by stage `type` (`DEEP`/`LIGHT`/`REM`/`ASLEEP`/`AWAKE`/`RESTLESS`) plus `minutesAsleep`/`minutesAwake`/`minutesInSleepPeriod`; there is **no efficiency field**, so CardiTrack derives it as asleep ÷ sleep period.
>
> **Three encodings hide behind those names, and every one fails silently if missed.** `int64` fields serialise as JSON **strings** under proto3 JSON (`"countSum": "9423"`), so a numeric-only parse reads them as absent. `Duration` fields serialise as strings with a mandatory **`s` suffix** (`"durationSum": "28800s"`) — `sedentary-period` is the one here, and parsing it as a bare number returns null on every wearer. And the enum members are per-schema: `ActiveMinutesRollupByActivityLevel.activityLevel` is `LIGHT`/`MODERATE`/`VIGOROUS`, *not* the `SEDENTARY`/`LIGHTLY_ACTIVE`/`MODERATELY_ACTIVE`/`VERY_ACTIVE` of the neighbouring `activity-level` type — borrowing that spelling matched no level and summed to zero. The **discovery document** (`https://health.googleapis.com/$discovery/rest?version=v4`, public, no auth) is the authority for all three: it gives each field's `format` and each enum's members, which the prose reference does not. Check it before adding a field, because `int64`, `google-duration` and `double` all look like "a number" in an example payload.
>
> **An absent rollup bucket is not a zero.** Google's own guidance is that a date missing from a rollup response means the device was not worn or has not synced, while a present `"countSum": "0"` is a true zero. CardiTrack keeps the two distinct all the way to the column: every activity metric is nullable, and `GoogleHealthApiClient` never coalesces. Collapsing them would corrupt both consumers — the multi-device merge takes the first non-null value, so a manufactured 0 from a higher-priority device beats another device's genuine reading, and the baseline averages unsynced days in as stillness, which is indistinguishable from the inactivity the product exists to detect.
>
> An earlier snake_case reading of the convention (`count`, `beatsPerMinute_avg`, `meters_sum`) matched nothing and returned **zeros rather than errors** — the whole class of bug this paragraph exists to prevent. All names, formats and enum members above are now checked against the discovery document and pinned by unit tests, but a schema cannot prove a field is *populated* for a given wearer's device: [`tools/HealthApiProbe`](../tools/HealthApiProbe/README.md) checks that against a live account.

### SSA Parameters

| Parameter | Recommended Value | Rationale |
|-----------|------------------|-----------|
| `window_size` (L) | `30` | 30-minute lag window captures ~2 cardiac cycles and one activity micro-burst |
| Number of components | `3` (Trend + 2 Oscillations) | Isolates circadian rhythm + short activity oscillation from noise |

### Implementation

> **Reference implementation** (Python, for algorithm *shape* only — nothing Python runs in production). The production pre-processor is `SsaDecomposition`: Broomhead–King lag-covariance + Math.NET symmetric EVD, not `pyts`'s trajectory-matrix SVD. Copying the snippet below will **not** reproduce `HrTrendLast` bit-for-bit; it illustrates grouping `[[0], [1, 2]]`, which production also uses.

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

> **Deployment note:** SSA runs inside the 5-minute assessor job (CPU only — no GPU required); a 60-sample decomposition is sub-millisecond, negligible against the window budget, and is the pre-filter that keeps MedGemma off ordinary windows.

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
> ~336 ms per request and ~508 MiB of held state for no benefit. That overhead is now switched off:
> the MedGemma container sets `LLAMA_ARG_CACHE_RAM=0` (llama.cpp's env equivalent of
> `--cache-ram`, where `0` disables), which stops the cache doing work whose result SWA guarantees
> will be thrown away. **That setting is paired with this finding** — if the model changes, or
> llama.cpp learns to restore SWA checkpoints, it becomes the wrong setting and should come off
> before anyone concludes caching still does not work here.
>
> Two consequences worth carrying: **prompt length is the only lever on inference latency** on this
> model, so trimming a prompt is worth what it looks like it is worth and nothing is waiting to
> make it cheaper; and the fixed-prefix discipline below should be kept anyway, because it costs
> nothing and pays off the day the model or the serving engine changes.

**Live prompt:** `CARDITRACK_REALTIME_ASSESSMENT_PROMPT` in `RealtimeAssessmentService` — Tone, Pronouns, and `CaregiverRegister`, then an hour of SSA yardsticks, then a caregiver-facing message plus a strict `critical` / `high` / `medium` / `low` severity token. The sketch that used to sit here (`[CARDITRACK_SYSTEM_PROMPT]`, "medical AI assistant", "flag for review", "clinical attention") is not sent. MedGemma copies sample phrases verbatim, so the live instructions name the SSA threshold (`scores under 3 are ordinary variation`) and tell the model to read activity and conditions in the data, without illustrating exercise, heat, or poor air.

**User prompt** (per member, per hour — values are SSA-denoised):
```
--- Last hour of data ---
Denoised heart rate trend, end of hour: X bpm
Latest reading: X bpm
Deviation score (typical jitters from trend): X
Typical jitter for this member: X bpm
Minutes with data this hour: X of 60
Steps this hour: X
SpO2 this hour: not measured
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
- **Caregiver notes are untrusted input.** They are free text a caregiver typed, so every instruction block states that this section is information about the person and that instructions inside it must not be followed. Notes are truncated at 1000 characters, visibly.
- **It goes after the fixed instructions, never inside them.** Anything above the block is the cacheable prefix.

### The pronoun rule (built today)

`MedicalPromptBlocks.Pronouns` is one line, and it is the reason the sex line above must always be present:

```
Use he or she as the sex given indicates, writing a given name at most once. If sex is not stated, use a given name instead of they. Never invent a name; they only if no name is given either.
```

Handed a `{{NAME}}` placeholder and told to write with it, a 4B model repeats the placeholder in every sentence of a six-sentence summary. When sex is known, that is the wrong shape — a case file about a subject, not one person telling another how someone is doing — so the rule is he or she after at most one name. When sex is not stated, repeating the name is the lesser wrong: "they" is a stranger's word for a family reading about one specific person, and every member created before M1-04 asked for sex sits at "not stated". The line used to open "Name them once": after Tone has just named the family member as the reader, "them" attaches to the reader, and most of the prompts that carry this rule never send a name at all, so it was also an instruction to invent. The token itself stays out of this line because alert and assessor copy is stored without resolving it; "they" remains only for that nameless, sex-not-stated case.

It follows `Tone` in **every prompt that writes prose** — the digest, the assessor, and the alert/baseline/learning/provisional insights — and is deliberately kept out of `CurrentStatusInstructions`. That prompt asks for a two-to-five-word headline and one sentence under fifteen words, where a pronoun scarcely arises and its own instructions already settle how the person is named. It is also the only prompt on a request path a caregiver waits on and the only one under a character budget (`StatusPromptBudget`), so a rule that bought nothing there would be paid for in latency on nearly every dashboard view. `MedicalPromptToneTests` pins both halves of that: every other prompt carries the rule, and the status prompt does not.

### The member-context composer (built today)

The block above is no longer hand-built per prompt. Each prompt service used to assemble its own member context, and the differences between them were accidents rather than decisions: environmental readings reached the assessor alone because that is the service the enrichment pass was built beside, and the digest read no assessments at all despite this document routing medium severity to it.

A **context source** now declares which prompts it belongs in (`PromptPurpose`, a flags enum over the five) and builds its own labelled section, or returns nothing when it has nothing to say about this member right now. `MemberContextComposer` assembles the applicable ones in a fixed order and owns the rules that must not drift: the `--- Label ---` delimiter, the per-section length cap, and defusing any line in a body that tries to open a section of its own. Sources fetch their own data, which is what makes adding one a single class and a single registration in `AddMedicalAiServices` rather than an edit to every prompt service.

Four are registered:

| Source | Reaches | Carries |
|---|---|---|
| `DemographicsContextSource` | all five | Age, sex, and the caregiver note — **decrypted**. `MedicalNotes` is encrypted at rest, and until this source existed every prompt passed the stored column straight through, so the model read a `v1:…` ciphertext envelope where the conditions and medication were meant to be |
| `EnvironmentalContextSource` | all five | Temperature, described conditions, humidity and air quality from the member's last GPS-tagged session, consent-gated, with a per-prompt staleness rule (3 h for the assessor, up to 48 h for a trend analysis) |
| `MonitoringContextSource` | digest | Yellow-and-above assessments from the last 24 h and unresolved alerts — the medium-severity route this document has always specified |
| `QuestionnaireAnswersContextSource` | all but the hero status line | The family's three most recent answers, as facts about the person — not a quiz transcript. The digest must use them to read the day, not retell them. A momentary answer is stamped with when the family gave it, because undated it reads as current and is not: "he had a busy day with chores", given about one day, came back the next morning as the explanation for a day the member had barely started. Standing facts stay undated — a date beside one invites the model to weigh it as news |

A source with nothing to say produces no heading at all, which is a stronger guarantee than instructing the model not to mention it: on a calm member the words are not in the prompt to be echoed.

`EnvironmentalContextSource` names its section differently for the assessor than for everything else. The assessor reads one hour and must not attribute this hour's heart rate to a session that ended two hours ago, so for it the heading stays "conditions during a recent exercise session". A digest describes a whole day and reads the same row as **the weather the person has been out in**; calling that a detail of the exercise invited the model to treat it as one. The family digest's instructions now ask for that reading explicitly — heat, cold, close air or poor air quality account for a harder-working heart or a quieter day, and are worth saying plainly when they do. Note that the `enrich` job that writes these rows is [not yet provisioned](#pipeline-components-role-breakdown-target-design), so for most members this section is still absent.

### Telling the model what time it is (built today)

Every prompt that describes "today" is describing a day still in progress, and the day rows carried exactly one word about that: `partial`. That is equally true at 07:00 and at 23:00, and it is not enough. Handed a running step total, a whole-day usual to read it against, and no clock, MedGemma drew the obvious wrong conclusion — one caregiver's 07:14 summary said their father's steps and active minutes had *decreased against his usual pattern*, on 26 steps taken since waking, while the dashboard hero above it read "Steps are lower today".

The deterministic layer was already careful here: `DigestInterpretationSignals.IsQuiet` refuses to call a partial day quiet before 16:00 local, and `DigestRefreshRules` keeps steps-decline on yesterday for the same reason. But the raw figures and the yardstick were both still in the prompt, side by side, so the model made the comparison the code declines to make. Guarding the computed observation and leaving the ingredients out in the open is not a guard.

`DigestDayProgress` closes it, in the pipeline's standing division of labour — .NET computes, the model only phrases. From the member's local clock and their baseline's own waking hours it produces the phrase that now qualifies today's row:

```
Today so far (2026-08-17, 07:14 local, about 0.2 hours since their usual waking time
of 07:00 — roughly 2% of their waking day has passed, so today's running totals cover
only that much of it and will keep rising; still in progress — activity totals are
partial; the sleep figure is last night's and complete): steps=26, HR_max=122
```

Both instruction blocks say the rest outright: today's steps and active minutes are a running total, to be read against how much of the waking day has gone and never against a whole-day usual, and never called low unless a computed observation says so. The hero prompt gets a one-line version of the same rule — it is the only prompt on a request path a caregiver waits on, and `StatusPromptBudget` went up by 50 characters to hold it, which is the budget working rather than failing.

The hero line also stopped anchoring "today" to UTC. `HealthInsightService` resolved a UTC civil day while the digest resolved the member's own through `MemberAnchorTimeZone`, so for a caregiver far enough east or west the two surfaces disagreed about which row was today.

### Not paying for a summary the day cannot support (built today)

The same value gates regeneration, because "there is not enough of this day yet" is one fact and stating it twice is how two rules drift apart.

`MinimumRegenerationInterval` assumes that data moving means the wording should move. Early in a member's day that inverts: the readings move because the day is filling up from nothing, so every pass finds new data and buys an inference to say the same thing about the same near-empty running total. With the half-hourly digest job and the assessor's immediate re-run, one member's morning produced a summary roughly every twenty minutes from local midnight — around twenty inferences before breakfast, each re-deriving that a just-woken person had not walked far.

Two gates now sit in front of that, both waived by everything that already waives the floor (an alert raised or resolved, a Yellow+ window, an SSA jump, a baseline divergence, a jump from yesterday) so a bad morning still reaches a caregiver at once:

- **Before the member's usual waking time**, a member who already has a summary gets no new one. There is no today to describe; yesterday's card is about yesterday and reads correctly as such at 03:00.
- **In the first three hours after waking**, a member who already has a summary *for the day in progress* falls under a two-hour floor instead of twenty minutes. The first summary of a new local day is never held back — a new day is new information by itself.

Neither gate applies to a member with no summary on file, the same stance the ordinary floor takes.

### Learning-phase and provisional prompts (built today)

Before a member has any `PatternBaseline` there is no normal to compare against, so `CARDITRACK_LEARNING_PROMPT` replaces the trend prompt and asks the model to describe what has been observed so far and what is still missing — call nothing unusual, without listing the words it must not use (MedGemma would echo them). The API reports this state as `isLearning` on the baseline-insight response, matching the dashboard's learning state so the two surfaces never disagree.

From about the first week, a **provisional** 7- or 14-day baseline exists before the 30-day one does. `CARDITRACK_PROVISIONAL_PROMPT` sits between the two framings: there is an early picture to compare against, so a comparison is an impression, not an established pattern, and a short window is not treated as settled. Sample hedges are not listed. The response carries `isProvisional`, again mirroring the dashboard. Provisional baselines colour dashboards and soften insight phrasing only — **they never feed alert thresholds** (see [alerts.md](./execution/backend/api/alerts.md)).

### The CardiJournal — the Daybook (built today)

The **CardiJournal** is the umbrella: the mobile tab, and the surface a tier buys more of. Inside it
sit cadence-named entries — the **Daybook** (one finished day, built today), and the **Weekbook** and
**Monthbook** (R2, sold and unbuilt; see [release_matrix.md](./release_matrix.md)). Each book is a
raw reassessment of its own period, written from that period's measurements — a Weekbook is not a
digest of seven Daybooks, so no imprecision propagates upward and a book still gets written for a
period whose lower books were skipped or discarded.

`CARDITRACK_DAYBOOK_PROMPT` — the account of one **finished** day, written once and never
recomputed. Everything else on this platform describes a day still in progress and is rewritten as
it moves; the Daybook is the opposite, and the difference drives every design choice below. It is
a **separate series** from the rolling family digest: the digest stays on member detail answering
"how are they doing right now", the Daybook is the finished-day record the Journal tab lists.

- **Storage.** `DigestAudience.Daybook`, alongside `Family` in the same partitioned
  `DigestEntries` table. The audience is part of the composite key and is persisted as its name,
  so the value cost no migration; a **partial unique index** (`EnforceOneDaybookPerDay`) holds the
  written-once contract against overlapping executions, with the insert absorbing the collision
  via a bare `ON CONFLICT DO NOTHING`.
- **Scheduling.** No job and no Cloud Scheduler entry of its own. `GenerateDueDaybooksAsync` runs
  inside the existing half-hourly `--job digest` execution, which already resolves each member's
  timezone: an entry is due when that member's local clock has passed their **write time** and
  none exists for the day before. That time defaults to **02:00** rather than midnight because a
  watch syncs on its own schedule and the last hours of a day routinely arrive after it — and what
  a Daybook misses it misses for good.
- **The write time is per member and caregiver-settable** (`CardiMember.DaybookLocalTime`, null =
  the default; `JournalSchedule` holds the default, the 01:00–12:00 window and the half-hour step).
  Read off the member the generator has already loaded, so honouring it costs no extra query on a
  pass that runs 48 times a day. It is a property of *whose* day it is, not of who is reading:
  a book is written once and read by every caregiver, so two of them cannot hold different times
  for it — which is why it sits on the member and why moving it needs manage access. The window is
  bounded and the step matches the job cadence for the reason above: a time the generator cannot
  honour would be a setting that quietly lies about itself. Contract in
  [cardimembers.md](./execution/backend/api/cardimembers.md).
- **Cost.** One MedGemma call per member per day, on an instance already warm from the digest
  pass; the existence probe is one indexed read on the other 47 passes. The prompt is the largest
  the platform sends (~4–8KB with a full day of rollups) — an accepted, explicit trade for
  completeness on the one generation that is asked to be complete.

**The whole day, assembled.** One pass gathers everything the platform holds about the reviewed
day, every fetch bounded by that day's own UTC window:

| Section | Source | Notes |
|---|---|---|
| The day in full | `ActivityLog` daily rollup vs the 30-day `PatternBaseline` and the published bands (NSF/AHA/WHO named) | absent readings say "not measured" |
| Devices line | `DeviceActivityLog` per-device day rows | which watch the readings came from |
| Hour by hour | `MetricRollupHourly` via the all-metrics range read | **quoted verbatim by explicit product decision** — the one exception to "code computes, model phrases"; the instructions bind the model to quote only figures that appear. Whole hours no metric covered are stated as gaps — computed deterministically, and only between hours that have data, so an unpopulated granular store is not mistaken for a day of silence |
| The day's monitoring | `Alert` rows attributed to the day via `AlertDetailComposer.AboutDate` + the day's Yellow+ `RealtimeAssessment` verdicts via the new range read | replaces `MonitoringContextSource` for this purpose — that source answers "the last 24h from now", the wrong clock for yesterday. The injection guardrail names the day-scoped label |
| Conditions during the day | `EnvironmentalReading` sessions overlapping the day, via the new overlap read | **consent-gated before the fetch** — withdrawing consent means the rows are not even read. Replaces `EnvironmentalContextSource` for this purpose |
| Family answers, demographics | `MemberContextComposer` as before | unchanged |

Every section degrades to absence rather than gating the entry, and the instructions turn absence
into "never mention it" rather than an invitation to invent.

**The register, and the line it turns on.** The journal's books are the prompts allowed clinical
vocabulary, and the allowance is bounded by a rule that is regulatory rather than stylistic: *a
precise term may name a measurement; it may never name a condition.* A term must also **explain
what it measures in plain words in the sentence that first uses it** — judged on first use only.
Both halves are enforced in code as well as asked for (`JournalRegisterGuards.NamesACondition`,
`.UnglossedTerm`, shared by every book so the line cannot drift between them; the sentence split ignores a full stop with a digit on both sides, so "95.4%"
cannot strand a gloss); a reply that trips either is discarded whole — nothing rather than
something wrong, and with more behind it here, since a discarded entry is not replaced half an
hour later. The condition list logs the phrase that tripped it, and deliberately excludes
**"consistent with"**: the prompt instructs the model to say where each reading sat against the
member's own usual, and that is one of the natural ways to answer it.

No question is asked off a Daybook. Questions exist to explain readings while they still matter,
and the answer would arrive a day after the day it was about.

### The CardiJournal — the Weekbook (built today)

`CARDITRACK_WEEKBOOK_PROMPT` — the account of one **finished week**, written once and never
recomputed, in the same register as the Daybook and at a different altitude.

- **A week is not a longer day.** The Daybook's job is completeness — every reading the day
  produced, hour by hour. The Weekbook's is **trajectory**: what moved against the member's usual
  across seven days, which day stood apart, what held steady. A prompt that listed seven days in
  turn would be seven Daybooks in a trench coat, and the caregiver already has those.
- **Raw reassessment, not a summary of summaries.** It is built from the week's own `ActivityLog`
  rows, its `PatternBaseline`, and its alerts and Yellow+ assessments — **never** from the week's
  Daybooks. Two payoffs, both load-bearing: an imprecise phrase in one Daybook cannot propagate
  upward, and a week whose Daybooks were skipped or discarded still gets its Weekbook. Pinned by a
  test that asserts the Daybook series is never read during a Weekbook run.
- **Storage.** `DigestAudience.Weekbook`, in the same partitioned `DigestEntries` table, dated by
  the week's **last day** — so one `LocalDate` identifies one week, and written-once is again a
  partial unique index on `(CardiMemberId, LocalDate)`. It needs its **own** index rather than a
  widened filter: a member has a Daybook and a Weekbook on the same date most weeks, so a single
  index across both audiences would refuse the second write. (EF keys unnamed indexes by their
  property set, so both are declared through the *named* `HasIndex` overload — the unnamed one
  reconfigures the first rather than adding a second, which scaffolds as "drop the Daybook index".)
- **Scheduling.** No job of its own, for the Daybook's reasons. `GenerateDueWeekbooksAsync` runs
  in the same half-hourly `--job digest` pass, and costs less than the Daybook does: it is due on
  one local weekday, so on six days in seven the per-member check stops at a date comparison
  before touching the database.
- **When.** Due on the member's own `JournalWeekStartsOn` (default Monday) once their local clock
  passes `WeekbookLocalTime` (default 02:00), covering the seven days ending the evening before.
  Both are per-member settings — see [cardimembers.md](./execution/backend/api/cardimembers.md).
- **The coverage guard.** At least **4 of the 7 days** must carry readings. A week measured on
  three days or fewer is not a quiet week, it is an unmeasured one, and an account of it would
  have to speak for the days that are missing — which reads to a caregiver as a verdict on the
  whole week. Silence must never read as healthy, so nothing is written and the gap is what the
  list screens say. Where a week does qualify, the coverage line states how many days it covered,
  and every average names the number of days behind it: an average of four nights and an average
  of seven are different claims.
- **Computed, not asked for.** Averages, the days-measured counts and the standout day are all
  arithmetic done here — the model phrases them and nothing else. The standout is the day furthest
  from the week's own average, claimed only where the week has ≥4 days of that reading and the day
  sits a fifth of the average or more away; an ordinary week produces no standout at all, which is
  the answer most weeks should give.
- **Read.** `?audience=weekbook` on both insights digest endpoints, alongside `family` and
  `daybook`, with the same `search`/`from`/`to`/`urgency` filters.

The register, its guards and the "counts, never scores" rule are shared with the Daybook
(`JournalRegisterGuards`); only the instruction-echo list is per-book, since it is drawn from the
wording of one brief.

### The CardiJournal — the Monthbook (built today)

`CARDITRACK_MONTHBOOK_PROMPT` — the account of one **finished calendar month**, written once on
the first of the next one.

- **The third altitude.** A Daybook is asked for completeness, a Weekbook for trajectory; a
  Monthbook is asked for **shape** — whether the month held together or came apart, which of its
  weeks differed, and what was true across all of them. Thirty days recited one by one would be
  unreadable, and four weeks recited one by one is a Weekbook the caregiver has already read.
- **Raw reassessment again.** Built from the month's own `ActivityLog` rows, baseline, alerts and
  Yellow+ assessments — never from its Weekbooks. The month's days are compressed to per-week
  aggregates *in code*, which is arithmetic over readings rather than a reading of anything the
  model wrote. Pinned by a test that neither the Weekbook nor the Daybook series is read.
- **The standout is a week, not a day.** At month scale a single unusual day is noise the
  caregiver has already seen in its Daybook. Weeks are cut from the month's first day in sevens
  rather than by weekday, so the comparison does not depend on where the member's journal week
  starts — this describes the month's own shape, not their week boundary. Claimed only where three
  weeks carry at least three measured days each and one sits a fifth of the average or more out.
- **Storage and scheduling.** `DigestAudience.Monthbook`, its own partial unique index
  (`IX_DigestEntries_OneMonthbookPerMonth`), dated by the month's last day, on the same half-hourly
  `--job digest` pass. Cheapest of the three books, and the only one that can skip a pass
  outright: on the days when no timezone on earth is on the first of a month — about
  twenty-nine in thirty — the job answers from an offset-span check without reading anything.
  The span is deliberately generous (UTC-12 to UTC+14, so up to three calendar dates at once):
  being wrong towards "possible" costs one pass that declines each member individually, while
  being wrong towards "impossible" would lose a member their book for good.
- **The coverage guard is 14 days** — about half a month, the same stance the Weekbook's
  four-of-seven takes at its own scale.
- **Retention does not bite, by construction.** The month is composed on the first day of the next
  one, when all of it is still inside every retention window. A month composed later could not say
  the same — and `DigestRetentionMonths` was raised 3 → 7 so the entries themselves survive the
  180-day history the top plan is sold on (see the DPIA, open item OI-14).

---

## Family Sharing: When and How to Push Data

Family members are secondary consumers of CardiTrack data — they care about the *wearer's* safety, not their own metrics. The system must translate clinical-flavoured MedGemma output into plain-language, actionable summaries, and must respect the wearer's explicit consent at every step.

### Consent and access model

CardiTrack uses the **caregiver-centric** model defined in the API spec ([cardimembers.md](./execution/backend/api/cardimembers.md), [family.md](./execution/backend/api/family.md)):

- The account **Admin** (caregiver) creates the CardiMember profile and — **per the designed consent endpoint, which is not yet built** — records the wearer's consent (per-metric: activity, heart rate, sleep). Today no `ConsentRecord` entity exists and no consent gating is enforced in the pipeline; the per-metric gate remains target design.
- Family members join by **Admin invitation** with a role: `admin`, `staff`, or `viewer`.
- Per-member **family routing rules** control who is pushed what (e.g. a sibling receives `red` only) — stored in the **planned `AlertPreferences` table** (not yet implemented).

Role → visibility mapping:

| Role | What they see |
|------|---------------|
| `viewer` (or routing-restricted member) | Push notifications per routing rules; read-only dashboard |
| `staff` / `admin` | Alerts + daily digest + trend charts + settings |

Raw metric values (exact bpm, SpO2 %) are **hidden by default** in family-facing pushes and digests regardless of role; an Admin can expose them per member. This reduces anxiety-driven misinterpretation by non-clinical family members. (Wearers never log in — pause monitoring and consent withdrawal are exercised by the caregiver on the wearer's behalf.)

---

### Trigger taxonomy: when to push

Each MedGemma response is parsed for a severity tag. The tag drives the push decision.

> **Severity mapping:** Critical/High/Medium/Low is the pipeline's **internal routing scale**. All user-facing surfaces (API, apps) use the product taxonomy: **Critical → `red`, High → `orange`, Medium → `yellow`, Low → `green` (health status; no alert emitted)**.

| Severity | MedGemma output signal | Family push? | Cadence |
|----------|----------------------|:---:|---------|
| **Critical** | Sustained HR anomaly, SpO2 < 90%, HR > 150 at rest | ✅ Immediate | Real-time (< 30 s) |
| **High** | HR trend deviation > 2 SD from 7-day baseline, HRV drop > 40% overnight | ✅ Immediate | Within 5-min window |
| **Medium** | Mild trend deviation, elevated resting HR for 2+ consecutive windows | ❌ Held | **Built:** carried into the family summary. `MonitoringContextSource` reads the member's yellow-and-above assessments from the last 24 h, and their unresolved alerts, into `CARDITRACK_FAMILY_DIGEST_PROMPT`. Until this existed the tier routed nowhere — alerts fire at orange and above, and the digest read no assessments at all |
| **Low / Normal** | No anomaly detected | ❌ | Silent; contributes to weekly trend |

(The former wearer-push column is gone with the wearer audience — wearers never log in and receive nothing.)

> The stored `HrDeviationScore` (the SSA deviation in noise-RMS units) is **evidence only and never overrides the model's verdict** — severity routes strictly on the parsed closing `Severity:` line. (An earlier design escalated on the LSTM's Δ anomaly score; that went with the LSTM on 2026-08-10.)

---

### Push channels and timing

```
MedGemma output (severity + plain-language summary)
  ↓
Severity router (Cloud Run)
  ├── Critical / High → FCM / APNs (push infra BUILT — FCM HTTP v1 relay with APNs
  │                     passthrough; wiring this router into it is what remains)
  │                  → SMS fallback if app not installed (future; provider not selected)
  ├── Medium          → read into the next family summary by the digest job (below)
  └── Low / Normal    → trend_aggregates only — no push
```

**Daily digest** (08:00 local time, family only — wearers never log in):
- Plain-language overnight summary: sleep quality, HRV trend, any medium events from the prior 24 h (**built** — see `DigestGenerator`; the job runs half-hourly against the member's day in progress rather than once at 08:00)
- Generated by a second MedGemma call with a digest-specific system prompt (see below)
- Delivered as push notification with deep link to trend chart

**Weekly trend report** (Monday 09:00 local time, family only — wearers never log in):
- 7-day cardiovascular trend: resting HR trajectory, HRV baseline shift, SpO2 stability
- Delivered to family members at "Full dashboard" level

---

### MedGemma prompt variants by audience

The system prompt changes depending on whether the output is destined for a clinician review queue, the wearer, or a family member. The **user prompt stays identical** — only the framing of the response changes.

**Family member prompt (live):** `CARDITRACK_ALERT_PROMPT` in `HealthInsightService` — Tone, Pronouns, `CaregiverRegister`, then an explanation of this alert and one specific action the caregiver can do now that answers it. The sketch that used to sit here (`[CARDITRACK_FAMILY_PROMPT]`, "non-medical family member", "check on their loved one", "Avoid clinical jargon") is not sent. Sample actions are not listed: MedGemma would repeat them.

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
- All AI-result rows are keyed by `CardiMemberId`. Family member reads are scoped by the `UserCardiMembers` relationship record in Cloud SQL — the query layer enforces this; there is no client-side filtering.
- Access is revocable at any time: an Admin removes the family member (or the wearer withdraws consent per metric); the relationship record is deleted and all future pushes for that pair stop immediately.
- Family-facing digests **do not include skin temperature** — this is too intimate a signal for a non-clinical audience and can cause disproportionate alarm.

---

## Trend Interpretation (formerly Predictive Monitoring)

Forward-looking awareness is CardiTrack's core market differentiator — every competitor reacts to emergencies; CardiTrack notices trajectories early. **Redesigned 2026-08-10:** the per-user LSTM risk model, its calibrated 0–100 risk scores, and the training/lifecycle machinery below it are dropped. In their place, the same early-warning value comes from three auditable layers:

1. **Deterministic trend features** — moving averages, slopes, and deviations computed in .NET from the multi-horizon rollups and R1 baselines (e.g. "resting HR up 6 bpm over 4 days against the 30-day baseline"). Code computes every number; nothing is estimated by a model.
2. **Pinned reference ranges** — a curated, versioned table of clinical norms (resting HR by age/sex, sleep-duration ranges, activity guidelines) sourced from named standards and injected into the prompt. The model never recalls benchmarks from its training data, so the yardstick behind every narrative is reviewable.
3. **MedGemma interpretation** — reads the computed features against the member's own history and the pinned ranges, and writes the family-facing trend narrative that feeds digests and insights.

### What is watched

The signal patterns worth narrating are unchanged — they are simply *computed as rules* now rather than predicted as scores:

| Pattern | Deterministic signal | What the family should take from it |
|---------|---------------------|--------------------------------------|
| **Possible illness onset** | Rising resting HR trend + declining HRV vs. baseline | A lay mention that they may be unwell — enough to react, not to treat |
| **Fatigue / overexertion** | Active zone minutes > personal 7-day average × 1.5 | That a quieter day would help |
| **Poor sleep pattern** | Elevated evening HR, late activity, short prior nights | That the evening before a short night is worth settling |
| **Cardiac trend** | 3+ day resting HR rise > 5 bpm or HRV decline > 30% from 30-day baseline | That this is a trend to keep an eye on |

These are the *product* meanings of each pattern, not phrases to put in the prompt. Sample copy in a MedGemma prompt comes back verbatim.

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
  (push dispatch joins once the pipeline's severity→push wiring lands —
   the FCM HTTP v1 relay itself is built)
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

**Trend prompt (design — not built).** When this ships it uses the same shared blocks as every other family-facing generation (`Tone`, `Pronouns`, `CaregiverRegister`). Sample hedges and example outputs are not listed: MedGemma copies them verbatim.

```
[CARDITRACK_TREND_PROMPT]
Describe this person's health trends for a family caregiver.
Use only the numbers and ranges provided; never supply your own.
Write a short trend note of two to three sentences.
Frame trajectories as possibilities, not certainties.
```

> Fixed-prefix and cacheable, the same as every other prompt in the registry.

---

### Updated prompt structure summary

| Prompt | Cadence | Audience | Purpose |
|--------|---------|----------|---------|
| `CARDITRACK_SYSTEM_PROMPT` | Every 5 min | Internal (clinical review queue) | **Superseded** by live `CARDITRACK_REALTIME_ASSESSMENT_PROMPT` (`RealtimeAssessmentService`): caregiver register, SSA yardsticks in, 1–3 caregiver-actionable sentences plus a strict severity token out |
| `CARDITRACK_LEARNING_PROMPT` | On request, before any baseline | Caregiver | What has been observed so far, before any baseline exists — **built today** |
| `CARDITRACK_PROVISIONAL_PROMPT` | On request, while only a 7/14-day baseline exists | Caregiver | Early impressions against a provisional baseline — **built today** |
| `CARDITRACK_FAMILY_PROMPT` | On high/critical events | Family members | **Superseded** by live `CARDITRACK_ALERT_PROMPT` (`HealthInsightService`) |
| `CARDITRACK_FAMILY_DIGEST_PROMPT` | Whenever the member's readings have moved past their last summary, or samples indicate a problem / diverge from baseline / jumped from yesterday, or their alert state changes (half-hourly job, and immediately after each assessor pass) | Family members | Headline, 2–5 sentence interpretation of the local day so far (usual pattern + steps walked as the movement yardstick for vitals; family answers used to read the day, never retold; a computed still-day/raised-rate pairing is injected when it fires), one supportive suggestion in plain language, and optionally one question for the family — **built today** (append-only store with history + API read; push pending). The suggestion must answer these readings and must never name or guess at a medical condition; naming a possible diagnosis stays the job of the alert/severity pipeline below, which never names a condition either — it prompts "reach out now" and surfaces one-tap contact, deliberately without a diagnostic guess. |
| ~~`CARDITRACK_DIGEST_PROMPT`~~ | — | ~~Wearer~~ | **Descoped 2026-08-10** — wearers never log in; self-monitoring is not the product |
| `CARDITRACK_TREND_PROMPT` | Daily (design) | Family members | Trend narrative over computed features + pinned reference ranges — replaces `CARDITRACK_PREDICT_PROMPT`, which died with the LSTM's risk scores. Same caregiver register as the live prompts; no sample hedges or example outputs |

---

## Cost Estimates

| Component | Estimated Cost | Notes |
|-----------|---------------|-------|
| Cloud Run — MedGemma (4 vCPU / 16 Gi, CPU always-allocated, 1 instance) | ~£150–175/mo when kept warm | Largest AI line item. Scale-to-zero keeps *idle* cost near zero but not *wake* cost: a cold start bills the full allocation for the ~150s the startup probe allows, so spend tracks **inference** cadence, not member count. The Aug 2026 dev overrun (~£13/day) was a `*/5` assessor that called MedGemma on every moved window; the SSA jump gate (`s >= 3`) is what makes that cadence affordable now — ordinary windows never reach the model |
| Cloud Run — pipeline services/jobs (CPU) | Near-zero at this scale | SSA pre-processor + trend-interpretation batch |
| Cloud Pub/Sub | ~£5–10/mo | Real-time ingestion buffer at ~333 events/s peak |
| Cloud SQL headroom (typed partitioned result tables) | Within existing instance | No separate data plane to pay for |
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
- The system prompt is identical across all users, but **prefix caching is not realisable on this model as served**: Gemma 3 uses sliding-window attention and `llama.cpp` will not restore a KV checkpoint under SWA (`LLAMA_ARG_CACHE_RAM=0` on the container; `cached n_tokens = 0` measured on every generation, 2026-08-13) — every call reprocesses the whole prompt from token zero. Keeping the prompt fixed and unpersonalised is prompt hygiene and auditability, not a caching win; prompt *length* is the only latency lever.
- The `googlehealth.location.readonly` scope the environmental-enrichment job needs has **not yet been requested from Google** — it is a new Restricted scope on top of the ones already granted, and requesting it re-opens the privacy & security review scope, not just an app update. Until it is granted, the `enrich` job's exercise fetch returns nothing for every connection (no connection carries the scope), which is a safe, silent no-op rather than a failure.
- Environmental-context enrichment is the platform's first geolocation data of any kind. It ships **consent-gated off by default** (`CardiMember.EnvironmentalContextConsentGranted`) precisely because the platform's broader per-metric consent architecture is still design-only (`docs/technical/data_protection_architecture.md` §8) — see that document and the DPIA for the compliance conditions this feature was built under.

---

*Version 2.3 — Last Updated: August 14, 2026*
