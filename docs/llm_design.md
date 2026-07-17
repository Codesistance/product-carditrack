# LLM Design — CardiTrack

## Overview

CardiTrack uses MedGemma 1.5 4B as its inference model for cardiovascular analysis of wearable data from up to 10,000 Fitbit devices. The AI pipeline runs two parallel paths: a real-time anomaly detection path (5-minute windows, SSA-LSTM pre-processing → MedGemma) and a daily predictive path (per-user LSTM risk model → MedGemma interpretation → family-facing health outlook). All pipeline logic runs on Azure Functions (CPU); only MedGemma inference runs on GPU via Azure Container Apps.

---

## Model

| Property | Value |
|----------|-------|
| Model | `google/medgemma-1.5-4b-it` |
| Version | MedGemma 1.5 (updated Jan 2026) |
| Parameters | 4B |
| Type | Multimodal instruction-tuned |
| Source | HuggingFace |

MedGemma 1.5 4B was chosen over the 27B variant for cost and latency reasons — at 4B parameters it fits on a single T4 GPU (~8GB in float16) with sufficient KV cache headroom for concurrent batched requests. It delivers improved accuracy on medical text reasoning and EHR understanding, both directly applicable to structured Fitbit time-series data.

> **⚠️ HuggingFace access required:** This model requires acceptance of the [Health AI Developer Foundations terms of use](https://huggingface.co/google/medgemma-1.5-4b-it) on HuggingFace before the weights can be pulled. Ensure `HF_TOKEN` is from an account that has accepted these terms.

---

## Infrastructure

### Service map

| Service | Role | SKU / Plan |
|---------|------|-----------|
| **Azure Container Apps** (GPU) | MedGemma 1.5 4B inference via vLLM | `Consumption-GPU-NC8as-T4` (T4 16 GB) |
| **Azure Functions** | All pipeline logic — webhook, aggregation, SSA-LSTM, predictive batch, digest, push | Consumption plan (CPU only) |
| **Azure Event Hubs** | Fitbit raw event stream buffer | Standard, 1 TU, 1 consumer group |
| **Azure Cosmos DB** | Results, prediction cards, trend store | Serverless (pay-per-RU) |
| **Azure SQL** (existing) | OAuth tokens (encrypted), user profiles, sensitivity settings, family relationships — the transactional system of record (see [infrastructure.md](./infrastructure.md#storage-boundary)) | Per infrastructure.md |
| **Azure Blob Storage** | Per-user LSTM model files (~50 KB each, ~500 MB at 10 K users) | Standard LRS, Hot tier |
| **Azure Notification Hubs** | FCM / APNs push routing for alerts and digests | Basic (free ≤ 1 M pushes/mo) |
| **Azure Key Vault** | `HF_TOKEN`, Fitbit client secret, Twilio API key | Standard |

---

### Azure Functions: role breakdown

Each function is a separate deployment within the same consumption plan. All are CPU-only — no GPU required outside of ACA.

| Function | Trigger | Cadence | Purpose |
|----------|---------|---------|---------|
| `FitbitWebhookReceiver` | HTTP | On event (~333/s peak) | Validates Fitbit signature header; forwards raw payload to Event Hubs |
| `FitbitAggregator` | Timer | Every 5 min | Reads Event Hubs → aggregates per-user window → runs SSA-LSTM pre-processor → calls MedGemma → writes result to Cosmos DB → routes severity |
| `SeverityRouter` | Cosmos DB trigger | On new result document | Reads severity tag; dispatches immediate push via Notification Hubs for Critical/High; queues Medium events for digest |
| `PredictiveFeatureAggregator` | Timer | Daily 03:00 local | Reads 30–90 day Cosmos DB history per user → computes daily feature vectors → runs per-user LSTM → applies confidence gate → writes prediction card to Cosmos DB |
| `PredictionCardPush` | Timer | Daily 06:00 local | Reads today's prediction cards → calls MedGemma (`CARDITRACK_PREDICT_PROMPT`) → pushes via Notification Hubs (risk ≥ 40) |
| `DigestGenerator` | Timer | Daily 08:00 local | Aggregates prior 24 h Cosmos events per user → calls MedGemma (`CARDITRACK_DIGEST_PROMPT` / `CARDITRACK_FAMILY_PROMPT`) → pushes via Notification Hubs |
| `InactivityDetector` | Timer | Every 15 min | Checks last event timestamp per user during waking hours (07:00–22:00); pushes rule-based "device check" if > 2 h silence — no MedGemma call |
| `ModelRetrainer` | Timer (dispatcher) | Weekly (Sunday 02:00) | Triggers a containerized **Python training job** (ACA job, CPU) that pulls 90-day feature history from Cosmos per user → retrains LSTM → exports **ONNX** model file to Blob Storage |

> **Runtime note:** all Azure Functions run on the **dotnet-isolated** runtime (.NET 10). LSTM/SSA *inference* inside Functions uses **ONNX Runtime**; model *training* (TensorFlow) runs only in the separate Python container job above. No mixed-runtime Function App.

> **Timeout note:** `FitbitAggregator` and `PredictiveFeatureAggregator` are the longest-running functions. Azure Functions consumption plan enforces a 10-minute maximum execution timeout — both are designed to process users in parallel batches and complete well within this limit at 10 K users.

---

### MedGemma on Azure Container Apps (GPU)

Azure App Service does not support GPUs. Azure Container Apps (ACA) with serverless GPU workload profiles provides the same managed, serverless feel — no cluster to operate, per-second billing, and scale-to-zero.

| Property | Value |
|----------|-------|
| Platform | Azure Container Apps |
| GPU | NVIDIA T4 (16 GB VRAM) — `Consumption-GPU-NC8as-T4` |
| Serving engine | vLLM (OpenAI-compatible endpoint) |
| Min replicas | 1 (keep warm — GPU cold start is 3–5 min) |
| Max replicas | 5 (autoscale on HTTP concurrency) |

**Why T4 over A100:** T4 is the cheapest GPU in ACA (~$0.59/hr vs ~$3.67/hr for A100). MedGemma 4B fits on 16 GB VRAM in float16 — A100 is unnecessary at this model size.

**vLLM flags:**
```
--model google/medgemma-1.5-4b-it
--dtype float16                  # T4 is compute capability 7.5; bfloat16 requires 8.0+
--enable-prefix-caching          # all four system prompts are fixed-prefix = big throughput win
--max-num-seqs 32
--gpu-memory-utilization 0.90
--max-model-len 8192
```

---

### Cosmos DB document structure

All data is partitioned by `wearerUserId` for efficient per-user queries.

| Collection | Partition key | Key fields | Retention |
|------------|--------------|-----------|-----------|
| `realtime-results` | `wearerUserId` | `windowStart`, `severity`, `medgemmaOutput`, `anomalyScores` | 90 days (TTL) |
| `prediction-cards` | `wearerUserId` | `date`, `riskScores`, `confidences`, `medgemmaOutput` | 90 days (TTL) |
| `trend-aggregates` | `wearerUserId` | `date`, `restingHR_7dMA`, `hrv_7dMA`, `sleepScore_7dMA` | 2 years |
| `digest-log` | `wearerUserId` | `date`, `audience`, `digestText` | 1 year |

---

### Deployment

#### Prerequisites

```bash
# Register required providers (once per subscription)
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.EventHub
az provider register --namespace Microsoft.DocumentDB
az provider register --namespace Microsoft.NotificationHubs
```

> **GPU quota:** Request `Consumption-GPU-NC8as-T4` quota before deploying — requires an Azure support ticket, allow 1–2 days. Recommended region: `swedencentral`.

#### Provision all services

```bash
LOCATION="swedencentral"
RG="carditrack-rg"
ENV="carditrack-env"
STORAGE="carditrackstorage"
EVENTHUB_NS="carditrack-eh"
COSMOS="carditrack-cosmos"
NOTIF_NS="carditrack-notif"
NOTIF_HUB="carditrack-hub"
BLOB="carditrackblob"
KV="carditrack-kv"
FUNC_APP="carditrack-functions"
ACA_APP="medgemma-inference"

az group create --name $RG --location $LOCATION

# Storage (Table + Blob in one account)
az storage account create \
  --name $STORAGE --resource-group $RG --location $LOCATION \
  --sku Standard_LRS --kind StorageV2

# Event Hubs
az eventhubs namespace create \
  --name $EVENTHUB_NS --resource-group $RG --location $LOCATION \
  --sku Standard --capacity 1

az eventhubs eventhub create \
  --name fitbit-raw \
  --namespace-name $EVENTHUB_NS --resource-group $RG \
  --partition-count 8 --message-retention 1

# Cosmos DB (serverless)
az cosmosdb create \
  --name $COSMOS --resource-group $RG \
  --capabilities EnableServerless \
  --default-consistency-level Session

az cosmosdb sql database create \
  --account-name $COSMOS --resource-group $RG \
  --name carditrack

for COLL in realtime-results prediction-cards trend-aggregates digest-log; do
  az cosmosdb sql container create \
    --account-name $COSMOS --resource-group $RG \
    --database-name carditrack --name $COLL \
    --partition-key-path /wearerUserId
done

# Notification Hubs
az notification-hub namespace create \
  --name $NOTIF_NS --resource-group $RG --location $LOCATION \
  --sku Basic

az notification-hub create \
  --name $NOTIF_HUB \
  --namespace-name $NOTIF_NS --resource-group $RG --location $LOCATION

# Key Vault
az keyvault create \
  --name $KV --resource-group $RG --location $LOCATION

# Azure Functions (consumption plan)
az functionapp create \
  --name $FUNC_APP --resource-group $RG \
  --storage-account $STORAGE \
  --consumption-plan-location $LOCATION \
  --runtime dotnet-isolated --runtime-version 10 \
  --functions-version 4

# ACA environment + GPU profile
az containerapp env create \
  --name $ENV --resource-group $RG --location $LOCATION

az containerapp env workload-profile add \
  --name $ENV --resource-group $RG \
  --workload-profile-name "NC8as-T4" \
  --workload-profile-type "Consumption-GPU-NC8as-T4"

# MedGemma inference container
az containerapp create \
  --name $ACA_APP \
  --resource-group $RG \
  --environment $ENV \
  --workload-profile-name "NC8as-T4" \
  --image vllm/vllm-openai:latest \
  --command "python,-m,vllm.entrypoints.openai.api_server" \
  --args "--model,google/medgemma-1.5-4b-it,--dtype,float16,--enable-prefix-caching,--max-num-seqs,32,--gpu-memory-utilization,0.90,--max-model-len,8192" \
  --env-vars "HF_TOKEN=secretref:hf-token" \
  --ingress internal --target-port 8000 \
  --min-replicas 1 --max-replicas 5 \
  --cpu 8 --memory 56Gi
```

> **Ingress change:** ACA ingress is set to `internal` — the vLLM endpoint is only reachable from within the ACA environment VNet. Azure Functions access it via the Functions VNet integration. It is not exposed to the public internet.

---

## AI Pipeline Overview

CardiTrack operates two parallel AI paths with distinct cadences and purposes:

```
┌─────────────────────────────────────────────────────────────┐
│                    REAL-TIME PATH (5-min)                   │
│                                                             │
│  Fitbit event → Event Hubs → Aggregator → SSA-LSTM          │
│  → MedGemma (anomaly) → Severity router → Alert / Digest   │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│               PREDICTIVE PATH (daily batch)                 │
│                                                             │
│  Cosmos DB (30-90 day history) → Feature aggregator         │
│  → Risk model (per-user LSTM) → MedGemma (interpretation)  │
│  → Prediction card → Wearer + Family digest                 │
└─────────────────────────────────────────────────────────────┘
```

The real-time path answers: *"Is something wrong right now?"*
The predictive path answers: *"Is something likely to go wrong in the next 24–72 hours?"*

---

## Data Ingestion Pipeline (Real-Time)

```
Fitbit devices (up to 10,000)
  ↓
Fitbit Subscriptions API (webhook push — no polling)
  ↓
Azure Function (HTTP trigger) — validates signature, forwards event
  ↓
Azure Event Hubs (fitbit-raw)
  ↓
Azure Function (timer, every 5 min) — aggregates per user
  ↓
SSA-LSTM pre-processor — denoises signal, extracts trend features
  ↓
ACA vLLM endpoint (MedGemma 1.5 4B)
  ↓
Azure Cosmos DB (results store)
```

---

## SSA-LSTM Pre-Processing Layer

Before each 5-minute aggregated window is sent to MedGemma, raw Fitbit time-series data passes through a Singular Spectrum Analysis + LSTM pipeline. SSA decomposes each metric into **Trend**, **Oscillation**, and **Noise** components, then the LSTM forecasts the next-window trend value. MedGemma receives the denoised trend values rather than raw averages, improving anomaly sensitivity.

### Role in the pipeline

| Stage | Input | Output |
|-------|-------|--------|
| SSA decomposition | Raw intraday time-series | Trend + Oscillation components per metric |
| LSTM forecast | Rolling trend history (look-back ~60 min) | Predicted next-window trend value |
| Deviation check | Predicted vs. actual trend | Δ anomaly score per metric |
| MedGemma prompt | Cleaned trend values + anomaly scores | Cardiovascular assessment |

### Fitbit JSON Field → SSA-LSTM Input Mapping

| Metric | Fitbit API Endpoint | JSON Path | Sampling Rate | SSA Input |
|--------|--------------------|-----------|----|-----------|
| Heart Rate (intraday) | `GET /activities/heart/date/{date}/1d/1min.json` | `activities-heart-intraday.dataset[].value` | 1-min intervals | Primary time-series for SSA decomposition |
| Resting Heart Rate | `GET /activities/heart/date/{date}/1d.json` | `activities-heart[0].value.restingHeartRate` | Daily scalar | Baseline anchor for HR trend |
| HRV (RMSSD) | `GET /hrv/date/{date}.json` | `hrv[0].value.dailyRmssd` | Daily scalar | Secondary series; supplement with `deepRmssd` |
| SpO2 (intraday) | `GET /spo2/date/{date}/all.json` | `minutes[].value` | ~5-min intervals | Upsample to 1-min via forward-fill before SSA |
| Steps (intraday) | `GET /activities/steps/date/{date}/1d/1min.json` | `activities-steps-intraday.dataset[].value` | 1-min intervals | Used as activity context feature alongside HR |
| Active Zone Minutes | `GET /activities/active-zone-minutes/date/{date}/1d/1min.json` | `activities-active-zone-minutes-intraday.dataset[].value` | 1-min intervals | Exogenous input to LSTM |
| Skin Temperature | `GET /temp/skin/date/{date}.json` | `tempSkin[0].value.nightlyRelative` | Daily scalar (nightly) | Early-warning feature; include when available |
| Sleep Stages | `GET /sleep/date/{date}.json` | `sleep[0].levels.summary.{deep,rem,light,wake}.minutes` | Daily summary | Context feature for next-day recovery model |

### SSA Parameters

| Parameter | Recommended Value | Rationale |
|-----------|------------------|-----------|
| `window_size` (L) | `30` | 30-minute lag window captures ~2 cardiac cycles and one activity micro-burst |
| Number of components | `3` (Trend + 2 Oscillations) | Isolates circadian rhythm + short activity oscillation from noise |
| LSTM `look_back` | `60` samples (60 min) | Sufficient history to detect slow-onset anomalies (e.g., rising resting HR) |
| LSTM hidden units | `64` | Balances capacity vs. inference latency on CPU (pre-processor runs on Azure Functions) |

### Implementation

> **Reference implementation** (Python, for algorithm clarity). The production pre-processor runs in **.NET (dotnet-isolated Functions)**: SSA implemented natively, LSTM inference via **ONNX Runtime** on models exported by the Python training job.

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

> **Deployment note:** The SSA-LSTM pre-processor runs inside the existing 5-minute Azure Function timer (CPU only — no GPU required). LSTM inference on a 60-sample sequence takes ~5ms on a consumption plan instance, well within the Function timeout.

### Why not Terra?

Terra provides a unified wearable API but costs $499+/month minimum — too expensive at 10,000 users. CardiTrack integrates directly with the Fitbit Subscriptions API.

### Why Event Hubs + 5-min batching?

10,000 devices at ~1 event/30s = ~333 events/s peak. Feeding each event directly to MedGemma would saturate the GPU. Batching per user over 5-minute windows reduces inference requests from ~333/s to a manageable ~33/s burst, significantly improving GPU utilisation and cost.

### Token storage

OAuth tokens for 10,000 Fitbit accounts are stored **encrypted in Azure SQL** (`DeviceConnections` table) — the transactional system of record. The pipeline reads them via the existing repository layer; `CardiTrack.Worker` owns proactive token refresh. See [infrastructure.md](./infrastructure.md#storage-boundary).

---

## Prompt Structure

Each inference request covers a single user's 5-minute aggregated window.

**System prompt** (fixed — benefits from vLLM prefix caching):
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

---

## Family Sharing: When and How to Push Data

Family members are secondary consumers of CardiTrack data — they care about the *wearer's* safety, not their own metrics. The system must translate clinical-flavoured MedGemma output into plain-language, actionable summaries, and must respect the wearer's explicit consent at every step.

### Consent and access model

CardiTrack uses the **caregiver-centric** model defined in the API spec ([cardimembers.md](./execution/backend/api/cardimembers.md), [family.md](./execution/backend/api/family.md)):

- The account **Admin** (caregiver) creates the CardiMember profile and records the wearer's consent (per-metric: activity, heart rate, sleep) via the consent endpoint. Data types without recorded consent are never processed by the pipeline or shared.
- Family members join by **Admin invitation** with a role: `admin`, `staff`, or `viewer`.
- Per-member **family routing rules** control who is pushed what (e.g. a sibling receives `red` only) — stored in `AlertPreferences`.

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
Severity router (Azure Function)
  ├── Critical / High → Azure Notification Hubs → FCM / APNs (immediate push)
  │                  → SMS fallback if app not installed (Twilio / Azure Communication Services)
  ├── Medium          → Cosmos DB queue (daily digest job reads at 08:00 local time)
  └── Low / Normal    → Cosmos DB (trend store only — no push)
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

> Both digest prompts are fixed per audience type and benefit from vLLM prefix caching, the same as the real-time monitoring prompt.

---

### Inactivity and device-off detection

A family member's greatest fear is silence — not knowing whether no news is good news or a missed alert. The system pushes a **"device check"** notification if:

- No Fitbit events received for a wearer for > 2 hours during expected active hours (07:00–22:00 local time)
- SpO2 or HR data absent from 3+ consecutive 5-minute windows

The notification reads: *"[Name]'s Fitbit hasn't synced in 2 hours. You may want to check in."* — this is rule-based, not MedGemma-generated, to keep latency and cost at zero for the common no-data case.

> This detector emits the standard **`device_disconnected`** alert (severity `yellow`) defined in [alerts.md](./execution/backend/api/alerts.md), so it appears in the alerts list, respects quiet hours/routing preferences, and follows the normal acknowledgment lifecycle. It is distinct from the `no_morning_activity` (`red`) alert, which fires when the device *is* syncing but no movement is detected past the typical wake time.

---

### Privacy guardrails

- Family members **never** receive the raw MedGemma inference output. A second, family-framed MedGemma call (or a template fill for low/normal windows) is always used.
- All Cosmos DB documents are partitioned by `wearerUserId`. Family member reads are scoped by the `UserCardiMembers` relationship record in Azure SQL — the query layer enforces this; there is no client-side filtering.
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
Cosmos DB (30–90 day per-user history)
  ↓
Daily feature aggregator (Azure Function, 03:00 local time)
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
  └── Risk score < 40 → silent (stored in Cosmos DB for trend view only)
```

---

### Per-user model: training and lifecycle

| Phase | Duration | Behaviour |
|-------|----------|-----------|
| **Cold start** | Days 1–29 | Real-time anomaly detection only. No predictions. App shows "Learning your patterns — predictions unlock on day 30." |
| **Bootstrap model** | Day 30 | First prediction model trained using 30-day feature history. Generic population priors used as regularisation. |
| **Personalized model** | Day 90+ | Model retrained weekly on rolling 90-day window. Day-of-week and seasonal effects modelled explicitly. |
| **Retraining trigger** | Any time | Major life event flag (user-reported illness, travel, device change) resets the baseline and pauses predictions for 7 days. |

Models are stored per user in Azure Blob Storage (one ~50KB serialised LSTM file per user). At 10,000 users this is ~500 MB — negligible. Retraining runs as a batch Azure Function on a consumption plan (CPU only, ~2s per model).

---

### False positive management

False positives are CardiTrack's primary churn risk (market target: <5% FP rate vs industry 20–30%). The predictive layer applies three controls:

**1. Confidence gate** — predictions with < 60% model confidence are suppressed entirely. A low-confidence window contributes to the trend view but does not push a notification.

**2. Consecutive signal requirement** — a risk score must exceed its threshold on 2 consecutive daily runs before a push notification is triggered. A single-day spike is logged but not surfaced.

**3. User-adjustable sensitivity** — Admins/Staff can set per-member sensitivity to Low / Medium / High (see [alerts.md](./execution/backend/api/alerts.md) alert-preferences). This shifts the risk score threshold for pushes (Low = 80+, Medium = 70+ [default], High = 50+). Sensitivity is stored in the `AlertPreferences` table in Azure SQL.

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

> Both variants are fixed-prefix prompts and benefit from vLLM prefix caching.

---

### Updated prompt structure summary

| Prompt | Cadence | Audience | Purpose |
|--------|---------|----------|---------|
| `CARDITRACK_SYSTEM_PROMPT` | Every 5 min | Internal (clinical review queue) | Real-time anomaly flagging |
| `CARDITRACK_FAMILY_PROMPT` | On high/critical events | Family members | Plain-language alert |
| `CARDITRACK_DIGEST_PROMPT` | Daily 08:00 | Wearer | 24h summary + medium events |
| `CARDITRACK_PREDICT_PROMPT` | Daily 06:00 | Wearer + family (risk ≥ 40) | Next-day health outlook |

---

## Cost Estimates

| Component | Estimated Cost | Notes |
|-----------|---------------|-------|
| ACA T4 GPU (1 replica, always-on) | ~$0.59/hr (~£430/mo) | Real-time MedGemma inference |
| ACA T4 GPU (scale-to-zero, active hours only) | ~$0.24/hr when active | Alternative if cost is priority |
| Azure Event Hubs (Standard) | ~£9/mo | Real-time ingestion |
| Azure Functions (consumption plan) | Near-zero at this scale | SSA-LSTM pre-processor + predictive batch |
| Azure Blob Storage (model store) | ~£0.50/mo | ~500 MB for 10,000 per-user LSTM models |
| Azure Cosmos DB | ~£20/mo | Results + prediction cards |
| Azure Notification Hubs | ~£5/mo | Push routing for family alerts |
| Fitbit API | Free | |
| Terra API | Not used — $499+/mo | |

---

## Important Caveats

- MedGemma is **not clinical-grade** out of the box. Outputs must be validated before use in any production health context.
- MedGemma 1.5 is **not optimised for multi-turn conversation**. Treat each inference request as stateless.
- All patient data processed through MedGemma must comply with applicable health data regulations (HIPAA, GDPR, etc.).
- The system prompt is identical across all users, making it an ideal candidate for vLLM prefix caching — ensure it is never personalised per user to preserve this benefit.
