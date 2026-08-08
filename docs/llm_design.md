# LLM Design — CardiTrack

> **STATUS — read this first**
>
> - **Built today:** MedGemma (Ollama-served `medgemma:4b` on Cloud Run) as the **Medical** AI provider and **Gemini 2.0 Flash** as the **General** provider, consumed by `GenerativeAiService`, `MedicalAiService`, `HealthInsightService`, and `ReportGenerationService` and surfaced through the API's **chat, insights, and reports** endpoints (`ChatController`, `InsightsController`, `ReportsController`). Insight prompts carry a **member context block** (age, sex, caregiver notes — never name or id) and switch to a **learning-phase variant** until a 30-day baseline exists. Ingestion is **30-minute polling** of the Google Health API by `WearableSyncWorker` in `CardiTrack.Worker`.
> - **Target architecture (this document):** the webhook-driven real-time pipeline, SSA-LSTM pre-processing, severity routing, digests, and predictive monitoring described below are the **design** for the GCP pipeline (Pub/Sub + Cloud Run) — they are **not built yet**. Push notification infrastructure (FCM/APNs) does not exist yet either.

## Overview

CardiTrack uses MedGemma as its inference model for cardiovascular analysis of wearable data from up to 10,000 wearable devices (Fitbit, Pixel Watch, and other sources connected through the Google Health API). The AI pipeline design runs two parallel paths: a real-time anomaly detection path (5-minute windows, SSA-LSTM pre-processing → MedGemma) and a daily predictive path (per-user LSTM risk model → MedGemma interpretation → family-facing health outlook). All pipeline logic runs on **Cloud Run services and jobs (CPU), scheduled by Cloud Scheduler**, in the same GCP project as the rest of the platform (`carditrack-490120`, `europe-west2`).

---

## Model

| Property | Value |
|----------|-------|
| Model | `medgemma:4b` (Ollama tag; MedGemma 4B instruction-tuned) |
| Parameters | 4B |
| Type | Multimodal instruction-tuned |
| Serving | Ollama on Cloud Run (CPU) — see [Infrastructure](#infrastructure) |

MedGemma 4B was chosen over the 27B variant for cost and latency reasons — at 4B parameters it runs on CPU-only Cloud Run today and would fit on a single T4 GPU (~8 GB in float16) if GPU serving is ever needed. It delivers strong accuracy on medical text reasoning and EHR understanding, both directly applicable to structured wearable time-series data.

---

## Infrastructure

### Service map (GCP)

| Service | Role | Status |
|---------|------|--------|
| **Cloud Run — `carditrack-<env>-medgemma`** | MedGemma inference via Ollama (CPU) | **Built** (prod deploy pipeline in place) |
| **Cloud Run services/jobs + Cloud Scheduler** | All pipeline logic — webhook receiver, aggregation, SSA-LSTM, predictive batch, digest, push dispatch | Target design |
| **Cloud Pub/Sub** (`carditrack-prod-realtime`) | Wearable raw event stream buffer | Topic provisioned (prod, `enable_pubsub`); pipeline not built |
| **Cloud SQL PostgreSQL (existing instance)** | OAuth tokens (encrypted AES-256-GCM in `DeviceConnections`), user profiles, sensitivity settings, family relationships — the transactional system of record (see [infrastructure.md](./infrastructure.md#storage-boundary)); plus **JSONB tables** for AI results (below) | Built (core schema); AI tables not built |
| **Google Cloud Storage** | Per-user LSTM model files (~50 KB each, ~500 MB at 10 K users) | Target design — no per-user model files exist yet |
| **FCM / APNs** | Push routing for alerts and digests | **Planned — no push infrastructure exists yet** |
| **Secret Manager** | Google Health API OAuth client secret, `gemini-api-key`, `medgemma-service-url` | Built |

Deliberate decision: AI results live in **PostgreSQL JSONB tables inside the existing Cloud SQL instance** rather than a separate document store — one data plane, one backup story, and family-read scoping can join directly against `UserCardiMembers`.

---

### Pipeline components: role breakdown (target design)

Each component is a Cloud Run service (event/HTTP-triggered) or Cloud Run job (Cloud Scheduler-triggered). All are CPU-only.

| Component | Trigger | Cadence | Purpose |
|-----------|---------|---------|---------|
| `HealthWebhookReceiver` | HTTP (Cloud Run service) | On event (~333/s peak) | Answers the Google Health API webhook verification handshake; acknowledges notifications with `204` and forwards the payload (user, data type, changed interval) to Pub/Sub |
| `WearableAggregator` | Cloud Scheduler | Every 5 min | Pulls Pub/Sub notifications → fetches changed data from the Google Health API per user/interval → aggregates per-user window → runs SSA-LSTM pre-processor → calls MedGemma → writes result to the `realtime_results` table → routes severity |
| `SeverityRouter` | On new result row | On write | Reads severity tag; dispatches immediate push via FCM/APNs for Critical/High; queues Medium events for digest |
| `PredictiveFeatureAggregator` | Cloud Scheduler | Daily 03:00 local | Reads 30–90 day history per user → computes daily feature vectors → runs per-user LSTM → applies confidence gate → writes prediction card |
| `PredictionCardPush` | Cloud Scheduler | Daily 06:00 local | Reads today's prediction cards → calls MedGemma (`CARDITRACK_PREDICT_PROMPT`) → pushes via FCM/APNs (risk ≥ 40) |
| `DigestGenerator` | Cloud Scheduler | Daily 08:00 local | Aggregates prior 24 h events per user → calls MedGemma (`CARDITRACK_DIGEST_PROMPT` / `CARDITRACK_FAMILY_PROMPT`) → pushes via FCM/APNs |
| `InactivityDetector` | Cloud Scheduler | Every 15 min | Checks last event timestamp per user during waking hours (07:00–22:00); pushes rule-based "device check" if > 2 h silence — no MedGemma call |
| `ModelRetrainer` | Cloud Scheduler (dispatcher) | Weekly (Sunday 02:00) | Triggers a containerized **Python training job** (Cloud Run job, CPU) that pulls 90-day feature history per user → retrains LSTM → exports **ONNX** model file to GCS |

> **Runtime note:** pipeline components run on **.NET**, matching the rest of the platform. LSTM/SSA *inference* uses **ONNX Runtime**; model *training* (TensorFlow) runs only in the separate Python container job above. No mixed-runtime services.
>
> **Timeout note:** `WearableAggregator` and `PredictiveFeatureAggregator` are the longest-running components. Cloud Run jobs allow generous task timeouts (up to 24 h), so both are designed to process users in parallel batches and complete comfortably at 10 K users.

> **Ingestion today:** until this pipeline is built, `WearableSyncWorker` in `CardiTrack.Worker` polls the Google Health API on a **30-minute cron**. Each device writes its own raw row to `DeviceActivityLogs` (one per device per day), and those are merged into `ActivityLogs` — one row per CardiMember per day, which is the series every reader and, in time, the SSA-LSTM pre-processor consumes. The merge coalesces each metric independently by device priority and never sums, so multiple wearables fill each other's gaps without double-counting. Each run re-fetches a short **trailing window** ending at yesterday (`SyncLookbackDays`, default 3) so a day missed during an outage is recovered rather than lost; a connection becomes due on its own `SyncFrequencyMinutes`, with the cron setting only how often the worker looks. The webhook path below replaces polling when it lands.
>
> Note that this polling path writes **only** to Cloud SQL — it does not publish to Pub/Sub, by design. The topic below carries provider webhook notifications forwarded by `HealthWebhookReceiver`, not `ActivityLogs` egress from the Worker; the Worker stays free of AI-pipeline responsibilities (see `CLAUDE.md`).

---

### MedGemma serving: what ships

MedGemma runs as the Cloud Run service `carditrack-<env>-medgemma`, provisioned by Terraform (`infrastructure/deployments/cloud_run.tf`) and deployed by CI:

| Property | Value |
|----------|-------|
| Platform | Cloud Run (**CPU** — no GPU) |
| Serving engine | **Ollama** (`ollama/ollama` base image; model baked in at build time) |
| Model tag | `medgemma:4b` — pinned in `src/Infrastructure/MedGemma/.model-version` |
| Resources | 8 vCPU / 16 Gi, `cpu_idle = false`, startup CPU boost |
| Scaling | Max **1 instance** (Ollama cannot safely multi-instance) |
| Ingress | Internal-only (VPC); port 8080 |
| Enablement | Service is created only when the `medgemma_image` tfvar is non-empty |

Deployment flow: the `deploy-medgemma` job in `.github/workflows/deploy-apps-prod.yml` deploys the image tag, then writes the resulting service URL to the Secret Manager secret `carditrack-<env>-medgemma-service-url`, which the API consumes as **`AI__Providers__0__BaseUrl`**. The API's provider configuration (Terraform-set env vars) selects MedGemma as `AI__MedicalProvider` and Gemini 2.0 Flash (`AI__Providers__1__*`, key from the `gemini-api-key` secret) as `AI__GeneralProvider`.

> **GPU scaling option (future):** if CPU latency becomes a bottleneck at scale, the same model can be served by vLLM on a single NVIDIA T4 (16 GB, float16 — the 4B model fits with KV-cache headroom), with `--enable-prefix-caching` exploiting the fixed system prompts, autoscaling on HTTP concurrency. This would require HuggingFace weights access (Health AI Developer Foundations terms) and a GPU-capable runtime (e.g. Cloud Run GPU or GKE Autopilot). Provisioning would be added to the existing Terraform — no imperative scripts.

---

### AI results: PostgreSQL JSONB tables (target design)

AI outputs are derived data in the **existing Cloud SQL instance** — regenerable, never authoritative. All tables are keyed by `wearer_user_id` for efficient per-user queries, with a JSONB payload column.

| Table | Key columns | JSONB payload | Retention |
|-------|-------------|---------------|-----------|
| `realtime_results` | `wearer_user_id`, `window_start`, `severity` | `medgemma_output`, `anomaly_scores` | 90 days (scheduled cleanup job) |
| `prediction_cards` | `wearer_user_id`, `date` | `risk_scores`, `confidences`, `medgemma_output` | 90 days |
| `trend_aggregates` | `wearer_user_id`, `date` | `resting_hr_7d_ma`, `hrv_7d_ma`, `sleep_score_7d_ma` | 2 years |
| `digest_log` | `wearer_user_id`, `date`, `audience` | `digest_text` | 1 year |

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
│  Wearable event → Pub/Sub → Aggregator → SSA-LSTM           │
│  → MedGemma (anomaly) → Severity router → Alert / Digest   │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│               PREDICTIVE PATH (daily batch)                 │
│                                                             │
│  Cloud SQL (30-90 day history) → Feature aggregator         │
│  → Risk model (per-user LSTM) → MedGemma (interpretation)  │
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
Cloud SQL JSONB tables (results store)
```

---

## SSA-LSTM Pre-Processing Layer

Before each 5-minute aggregated window is sent to MedGemma, raw wearable time-series data passes through a Singular Spectrum Analysis + LSTM pipeline. SSA decomposes each metric into **Trend**, **Oscillation**, and **Noise** components, then the LSTM forecasts the next-window trend value. MedGemma receives the denoised trend values rather than raw averages, improving anomaly sensitivity.

### Role in the pipeline

| Stage | Input | Output |
|-------|-------|--------|
| SSA decomposition | Raw intraday time-series | Trend + Oscillation components per metric |
| LSTM forecast | Rolling trend history (look-back ~60 min) | Predicted next-window trend value |
| Deviation check | Predicted vs. actual trend | Δ anomaly score per metric |
| MedGemma prompt | Cleaned trend values + anomaly scores | Cardiovascular assessment |

### Google Health API Data Type → SSA-LSTM Input Mapping

The Google Health API consolidates the legacy per-endpoint surface into **data types** queried via `list` (intraday/granular) and `rollUp`/`dailyRollUp` (summaries) at `https://health.googleapis.com`.

Methods follow the v4 REST shape: `GET /v4/users/me/dataTypes/{type}/dataPoints` (list) and `POST .../dataPoints:dailyRollUp` (daily summary; heart-rate/active-minutes/total-calories rollups max 14-day range, others 90 days).

A `dailyRollUp` body takes `range` as a **`CivilTimeInterval`** — closed-open, with `start`/`end` each a **`CivilDateTime`**, meaning the calendar date nests under `date` (`{"start": {"date": {"year": …, "month": …, "day": …}}}`) and `time` is omitted to mean midnight. A bare `{year, month, day}` at `range.start` is rejected with `INVALID_ARGUMENT` field violations — the shape `FitbitApiClient` sent until it was corrected against the live API.

| Metric | Data type / method | Sampling Rate | SSA Input |
|--------|--------------------|----|-----------|
| Heart Rate (intraday) | `heart-rate` — `list` | 1-min intervals | Primary time-series for SSA decomposition |
| Resting Heart Rate | `resting-heart-rate` — `dailyRollUp` | Daily scalar | Baseline anchor for HR trend |
| HRV (RMSSD) | `daily-heart-rate-variability` — `dailyRollUp` (granular: `heart-rate-variability` — `list`) | Daily scalar | Secondary series |
| SpO2 (intraday) | `oxygen-saturation` — `list` | ~5-min intervals | Upsample to 1-min via forward-fill before SSA |
| Steps (intraday) | `steps` — `list` | 1-min intervals | Used as activity context feature alongside HR |
| Active Zone Minutes | `active-zone-minutes` — `list` | 1-min intervals | Exogenous input to LSTM |
| Skin Temperature | skin-temperature data type — `dailyRollUp` | Daily scalar (nightly) | Early-warning feature; include when available |
| Sleep Stages | `sleep` — `list` (session-shaped) | Daily summary | Context feature for next-day recovery model |

> Rollup responses carry a union value per data type (e.g. `steps.count`, `heartRate.beatsPerMinute_min/max/avg` — both verified against the v4 reference). Remaining field names in `FitbitApiClient` marked "(assumed)" follow the documented `{field}_{aggregation}` convention and need live-sandbox confirmation once console access exists. Those are **response** names only: the request body is verified, and a unit test pins the `range` shape so it cannot regress silently.

### SSA Parameters

| Parameter | Recommended Value | Rationale |
|-----------|------------------|-----------|
| `window_size` (L) | `30` | 30-minute lag window captures ~2 cardiac cycles and one activity micro-burst |
| Number of components | `3` (Trend + 2 Oscillations) | Isolates circadian rhythm + short activity oscillation from noise |
| LSTM `look_back` | `60` samples (60 min) | Sufficient history to detect slow-onset anomalies (e.g., rising resting HR) |
| LSTM hidden units | `64` | Balances capacity vs. inference latency on CPU (pre-processor runs on CPU Cloud Run) |

### Implementation

> **Reference implementation** (Python, for algorithm clarity). The production pre-processor runs in **.NET**: SSA implemented natively, LSTM inference via **ONNX Runtime** on models exported by the Python training job.

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

> **Deployment note:** The SSA-LSTM pre-processor runs inside the 5-minute aggregator (CPU only — no GPU required). LSTM inference on a 60-sample sequence takes ~5ms, negligible against the window budget.

### Why not Terra?

Terra provides a unified wearable API but costs $499+/month minimum — too expensive at 10,000 users. CardiTrack integrates directly with the Google Health API, whose webhook subscriptions are free and already aggregate Fitbit, Pixel Watch, and connected third-party sources.

### Why Pub/Sub + 5-min batching?

10,000 devices at ~1 event/30s = ~333 events/s peak. Feeding each event directly to MedGemma would saturate the inference service. Batching per user over 5-minute windows reduces inference requests from ~333/s to a manageable ~33/s burst, significantly improving utilisation and cost — especially important while MedGemma runs as a single CPU instance.

### Token storage

Google-issued OAuth tokens for device connections are stored **encrypted (AES-256-GCM) in Cloud SQL** (`DeviceConnections` table) — the transactional system of record. The pipeline reads them via the existing repository layer; `CardiTrack.Worker` owns proactive token refresh. See [infrastructure.md](./infrastructure.md#storage-boundary).

---

## Prompt Structure

Each inference request covers a single user's 5-minute aggregated window.

**System prompt** (fixed — a fixed prefix benefits from serving-engine prompt caching):
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

- **Age and sex only, never name or id.** Neither identifier changes the clinical reading, so neither is sent. `Other`/`Prefer not to say` are omitted rather than passed through — they tell the model nothing it can use.
- **Caregiver notes are untrusted input.** They are free text a caregiver typed, so every instruction block states that this section is background information and that instructions inside it must not be followed. Notes are truncated at 1000 characters, visibly.
- **It goes after the fixed instructions, never inside them.** Anything above the block is the cacheable prefix.

### Learning-phase prompt (built today)

Before a member has a 30-day `PatternBaseline` there is no normal to deviate from, so `CARDITRACK_LEARNING_PROMPT` replaces the trend prompt and asks the model to describe what has been observed so far and what is still missing — explicitly forbidding words like *elevated*, *low*, or *deviation*. The API reports this state as `isLearning` on the baseline-insight response, matching the dashboard's learning state so the two surfaces never disagree.

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
| **Medium** | Mild trend deviation, elevated resting HR for 2+ consecutive windows | ❌ Held | ✅ In-app | Included in daily digest |
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
  ├── Medium          → digest queue (daily digest job reads at 08:00 local time)
  └── Low / Normal    → trend_aggregates only — no push
```

**Daily digest** (08:00 local time, family + wearer):
- Plain-language overnight summary: sleep quality, HRV trend, any medium events from the prior 24 h
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

**Wearer system prompt (daily digest):**
```
[CARDITRACK_DIGEST_PROMPT]
You are summarising the past 24 hours of a user's cardiovascular wearable data.
Highlight any notable events from today. Describe overnight heart rate and HRV trends.
Be encouraging where metrics are healthy. Flag concerns clearly but without alarm.
Suggest one actionable next step if a pattern warrants it (e.g., "consider an earlier bedtime tonight").
```

> Both digest prompts are fixed per audience type, keeping them cacheable as fixed prefixes, the same as the real-time monitoring prompt.

---

### Inactivity and device-off detection

A family member's greatest fear is silence — not knowing whether no news is good news or a missed alert. The system pushes a **"device check"** notification if:

- No wearable events received for a wearer for > 2 hours during expected active hours (07:00–22:00 local time)
- SpO2 or HR data absent from 3+ consecutive 5-minute windows

The notification reads: *"[Name]'s device hasn't synced in 2 hours. You may want to check in."* — this is rule-based, not MedGemma-generated, to keep latency and cost at zero for the common no-data case.

> This detector emits the standard **`device_disconnected`** alert (severity `yellow`) defined in [alerts.md](./execution/backend/api/alerts.md), so it appears in the alerts list, respects quiet hours/routing preferences, and follows the normal acknowledgment lifecycle. It is distinct from the `no_morning_activity` (`red`) alert, which fires when the device *is* syncing but no movement is detected past the typical wake time.

---

### Privacy guardrails

- Family members **never** receive the raw MedGemma inference output. A second, family-framed MedGemma call (or a template fill for low/normal windows) is always used.
- All AI-result rows are keyed by `wearer_user_id`. Family member reads are scoped by the `UserCardiMembers` relationship record in Cloud SQL — the query layer enforces this; there is no client-side filtering.
- Access is revocable at any time: an Admin removes the family member (or the wearer withdraws consent per metric); the relationship record is deleted and all future pushes for that pair stop immediately.
- Family-facing digests **do not include skin temperature** — this is too intimate a signal for a non-clinical audience and can cause disproportionate alarm.

---

## Predictive Monitoring

Predictive monitoring is CardiTrack's core market differentiator — every competitor reacts to emergencies; CardiTrack warns before they happen. This section defines what is predicted, when, how the AI pipeline produces predictions, and how confidence is managed to keep false positives below 5%.

### What the model predicts

Predictions are scoped to the 24–72 hour horizon. Longer horizons have insufficient signal fidelity from consumer wearables; shorter horizons are covered by real-time anomaly detection.

| Prediction | Input signals | Target users | Horizon |
|------------|--------------|--------------|---------|
| **Illness onset** | Rising resting HR + declining HRV + elevated skin temp Δ | All | 24–48 h |
| **Fatigue / overexertion** | Active zone minutes > personal 7-day average × 1.5, HRV drop | Active elderly | 12–24 h |
| **Poor sleep forecast** | Elevated evening HR, high step count late in day, low prior-night HRV | All | Tonight |
| **Elevated fall risk** | Poor overnight sleep quality → daytime cognitive/motor impairment | 70+ users | Same day |
| **Cardiac trend alert** | 3+ day resting HR rise > 5 bpm or HRV decline > 30% from 30-day baseline | All | 24–72 h |

> **What is never predicted:** Specific diagnoses, medication interactions, or acute cardiac events (these require clinical-grade devices and are outside CardiTrack's scope). Outputs are framed as risk indicators, not clinical predictions.

---

### Predictive AI pipeline

```
Cloud SQL (30–90 day per-user history)
  ↓
Daily feature aggregator (Cloud Run job, 03:00 local time)
  — Computes: resting HR 7d MA, HRV 7d MA, sleep score 7d MA,
              active minutes 7d MA, skin temp delta (if available),
              day-of-week seasonality index
  ↓
Cold start check
  ├── < 30 days data → no prediction (baseline learning mode)
  └── ≥ 30 days data → risk model inference
  ↓
Per-user risk model (LSTM, 64 hidden units, look-back = 30 days)
  — Outputs: risk score (0–100) per prediction category
             + predicted next-day values for resting HR, HRV, sleep score
             + 80% confidence interval per predicted value
  ↓
Confidence gate
  ├── Confidence < 60% → suppress prediction (insufficient signal)
  └── Confidence ≥ 60% → pass to MedGemma
  ↓
MedGemma (CARDITRACK_PREDICT_PROMPT) — interprets risk scores
  — Generates plain-language "prediction card"
  ↓
Routing
  ├── Risk score ≥ 70 → prediction card in wearer's morning push + family digest
  ├── Risk score 40–69 → prediction card in wearer's morning push only
  └── Risk score < 40 → silent (stored in trend_aggregates for trend view only)
```

---

### Per-user model: training and lifecycle

| Phase | Duration | Behaviour |
|-------|----------|-----------|
| **Cold start** | Days 1–29 | Real-time anomaly detection only. No predictions. App shows "Learning your patterns — predictions unlock on day 30." |
| **Bootstrap model** | Day 30 | First prediction model trained using 30-day feature history. Generic population priors used as regularisation. |
| **Personalized model** | Day 90+ | Model retrained weekly on rolling 90-day window. Day-of-week and seasonal effects modelled explicitly. |
| **Retraining trigger** | Any time | Major life event flag (user-reported illness, travel, device change) resets the baseline and pauses predictions for 7 days. |

The design stores models per user in **Google Cloud Storage** (one ~50 KB serialised LSTM file per user — ~500 MB at 10,000 users, negligible). Retraining runs as a batch Cloud Run job (CPU only, ~2s per model). **No per-user model files exist yet** — this ships with the predictive path.

---

### False positive management

False positives are CardiTrack's primary churn risk (market target: <5% FP rate vs industry 20–30%). The predictive layer applies three controls:

**1. Confidence gate** — predictions with < 60% model confidence are suppressed entirely. A low-confidence window contributes to the trend view but does not push a notification.

**2. Consecutive signal requirement** — a risk score must exceed its threshold on 2 consecutive daily runs before a push notification is triggered. A single-day spike is logged but not surfaced.

**3. User-adjustable sensitivity** — Admins/Staff can set per-member sensitivity to Low / Medium / High (see [alerts.md](./execution/backend/api/alerts.md) alert-preferences). This shifts the risk score threshold for pushes (Low = 80+, Medium = 70+ [default], High = 50+). Sensitivity will be stored in the planned `AlertPreferences` table in Cloud SQL (not yet implemented).

---

### MedGemma prompt variant: predictions

A separate system prompt ensures predictive output is framed as forward-looking guidance, not a current-state alarm.

**Predictive system prompt:**
```
[CARDITRACK_PREDICT_PROMPT]
You are an AI health assistant generating a next-day health outlook for a user's family caregiver app.
You have been given risk scores and predicted metric values for the next 24 hours.
Write a short, plain-language "health outlook" (2–3 sentences max).
Frame predictions as possibilities, not certainties: "may", "could", "worth watching".
If risk is low across all categories, lead with reassurance.
If one category is elevated, mention it gently and suggest one practical action.
Never mention specific risk score numbers. Never diagnose. Never alarm.
```

**Example output for a high fatigue risk day:**
> "Based on recent activity levels, [Name] may feel more tired than usual today — a lighter day could help. Heart rate and sleep patterns look broadly stable. Nothing urgent, but a check-in this afternoon might be welcome."

**Example output for a low-risk day:**
> "[Name]'s health patterns look settled for today. Resting heart rate and sleep quality have been consistent this week — a good sign."

> Both variants are fixed-prefix prompts, cacheable by the serving engine.

---

### Updated prompt structure summary

| Prompt | Cadence | Audience | Purpose |
|--------|---------|----------|---------|
| `CARDITRACK_SYSTEM_PROMPT` | Every 5 min | Internal (clinical review queue) | Real-time anomaly flagging |
| `CARDITRACK_LEARNING_PROMPT` | On request, first ~30 days | Caregiver | What has been observed so far, before any baseline exists — **built today** |
| `CARDITRACK_FAMILY_PROMPT` | On high/critical events | Family members | Plain-language alert |
| `CARDITRACK_DIGEST_PROMPT` | Daily 08:00 | Wearer | 24h summary + medium events |
| `CARDITRACK_PREDICT_PROMPT` | Daily 06:00 | Wearer + family (risk ≥ 40) | Next-day health outlook |

---

## Cost Estimates

| Component | Estimated Cost | Notes |
|-----------|---------------|-------|
| Cloud Run — MedGemma (8 vCPU / 16 Gi, CPU always-allocated, 1 instance) | ~£300–350/mo when kept warm | Largest AI line item; scale-to-zero in dev keeps idle cost near zero |
| Cloud Run — pipeline services/jobs (CPU) | Near-zero at this scale | SSA-LSTM pre-processor + predictive batch |
| Cloud Pub/Sub | ~£5–10/mo | Real-time ingestion buffer at ~333 events/s peak |
| Cloud SQL headroom (JSONB result tables) | Within existing instance | No separate data plane to pay for |
| GCS (per-user model store) | ~£0.50/mo | ~500 MB for 10,000 per-user LSTM models |
| Gemini 2.0 Flash API | Usage-based, small | General-provider calls (chat/reports) |
| Google Health API | Free | Restricted scopes — production access requires Google's privacy & security review |
| Terra API | Not used — $499+/mo | |

---

## Important Caveats

- MedGemma is **not clinical-grade** out of the box. Outputs must be validated before use in any production health context.
- MedGemma is **not optimised for multi-turn conversation**. Treat each inference request as stateless.
- All patient data processed through MedGemma must comply with applicable health data regulations (HIPAA, GDPR, etc.).
- All Google Health API scopes are classified **Restricted** — production (verified) access requires passing Google's privacy & security review; before verification, only enrolled test users can connect devices.
- The system prompt is identical across all users, making it an ideal candidate for serving-engine prefix caching — ensure it is never personalised per user to preserve this benefit.

---

*Version 2.0 — Last Updated: August 7, 2026*
