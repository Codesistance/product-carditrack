# MedGemma serving architecture — CPU Cloud Run, and what replaces it

**Status:** Proposed (2026-08-19) — MS-2 resolved 2026-08-19, option B selected; remaining decisions in §6
**Scope:** Where MedGemma inference runs and what it costs. Covers the Cloud Run CPU service as deployed, the measured failure mode (HTTP 429), and the GPU options. Does **not** cover which model is served, prompt content, or the SSA contract.
**Relationship to other docs:** [llm_design.md](../llm_design.md) owns the SSA → MedGemma contract and first sketched the GPU option. [apm_setup_runbook.md](./apm_setup_runbook.md) owns the client-side telemetry these numbers come from. [dpia.md](../compliance/dpia.md) owns residency and the US transfer surface (OI-5). The `medgemma_min_instances` comment in [cloud_run.tf](../../infrastructure/deployments/cloud_run.tf) owns the warm-instance economics and stays correct — this document changes the compute underneath it, not that reasoning.

---

## 1. Context — what is actually deployed

`carditrack-dev-medgemma` serves `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` (4-bit quantised, ~3 GB) on **stock Ollama** — `ollama/ollama:latest`, `ENTRYPOINT ["ollama", "serve"]` ([Dockerfile](../../src/Infrastructure/MedGemma/Dockerfile)). Not vLLM; vLLM appears in this repo only as a future option in `llm_design.md`.

Read off the live service on 2026-08-19:

| Setting | Value | Source |
|---|---|---|
| CPU / memory | 4 vCPU / 16 GiB, `cpu_idle = false` | Terraform + live revision |
| Scaling | `min = 1`, `max = 1` | `max` from `medgemma_max_instances` (default 1); `min` from `medgemma_min_instances`, whose default is **0** and which `environments/dev.tfvars` sets to 1 — the warm instance is a dev-only choice, not the variable's default |
| Request concurrency | **640** | live revision — Terraform sets none, so this is a platform default, not a considered value |
| Request timeout | **300s** | live revision — identical to the client's `PrivateAiSettings.TimeoutSeconds` |
| Container image | unchanged since 2026-08-10 (same digest) | revision list |
| Region | `europe-west2` | `infrastructure/variables.tf` |

Measured over 2026-08-09 → 08-19 (Cloud Monitoring, unsampled):

| Metric | Value |
|---|---|
| Billable instance time | **86,326–86,409 s/day** — a full 24h, every day |
| Successful requests | 251–434/day |
| Refused requests (4xx) | 0–6/day through 08-16, then **113–215/day** |
| CPU utilisation p99 | **0.94–0.995** |
| Inference p50 | 15.1s (08-12) → 19.4s (08-13) → 93.7s (08-15) → **123.9s** (08-19) |
| Inference p95 | **216.3s** (08-19) |

## 2. The failure mode

Every refusal is Cloud Run's, not Ollama's, and carries its own reason string at **0s latency**:

> `The request was aborted because there was no available instance.`

Rejected instantly, never queued. Ollama signals a full queue with 503; across the window there were no 503s from MedGemma at all. The mechanism is:

1. `max_instance_count = 1` — the service cannot scale out, by deliberate choice ("Ollama cannot safely multi-instance").
2. The single instance runs at ~99% CPU, because CPU inference on a 4B model saturates 4 vCPU.
3. Cloud Run's autoscaler wants another instance, cannot have one, and refuses the overflow.

Request concurrency of 640 is irrelevant to this: the rejection is the autoscaler hitting a ceiling, not a concurrency limit being reached. Nothing about the current configuration expresses an intended admission policy — 640 is simply what the platform defaults to when Terraform stays silent.

**Two independent changes stacked to produce it:**

- **2026-08-14** — the caregiver-register prompt rewrites (PR #296) took p50 inference from ~19s to ~94s. Harmless on its own: at ~13 calls/hour the instance still had slack, and refusals stayed near 1.5%.
- **2026-08-17, 12:00–13:00 UTC** — PR #350 (day-aware summaries) deployed and tripled call volume, 13/hr → 49/hr. Refusals went from ~0/hr to 55/hr in a single hour. dea5e347 ("Stop regenerating family summaries through the night") landed at 13:18 UTC and brought volume back down, but latency has kept climbing since, so the service still sits saturated.

The duty cycle explains why there is no headroom: ~290 calls × ~124s ≈ **36,000s of real inference against 86,400s billed** — 42% busy, 100% paid for, and bursty enough that the overlaps are constant.

## 2a. Who calls MedGemma, and how often

Six call sites, three triggers. Everything below the line is per **CardiMember**; dev has three that actively generate.

```
                    ┌──────────────────────────────────────────────────────┐
                    │  MedGemma — Ollama, medgemma-1.5-4b-it Q4_K_M        │
                    │  Cloud Run · 4 vCPU / 16 GiB · CPU · europe-west2    │
                    │  min = max = 1 · concurrency 640 · timeout 300s      │
                    └───────────────────────▲──────────────────────────────┘
                                            │
                     435 hits/day observed  │  278 × 200   141 × 429   16 × 504
                                            │  (see 504 note below)
        ┌───────────────────────────────────┼───────────────────────────────────┐
        │                                   │                                   │
┌───────┴────────┐                ┌─────────┴─────────┐              ┌──────────┴─────────┐
│  REQUEST PATH  │                │   assess  job     │              │   digest  job      │
│  CardiTrack.API│                │  2-59/5 * * * *   │              │   */30 * * * *     │
│  on demand     │                │  288 runs/day     │              │   48 runs/day      │
└───────┬────────┘                └─────────┬─────────┘              └──────────┬─────────┘
        │                                   │                                   │
        │                         ┌─────────┴─────────┐          ┌──────┬───────┼───────┬────────┐
        │                         │                   │          │      │       │       │        │
        ▼                         ▼                   ▼          ▼      ▼       ▼       ▼        ▼
  HealthInsight            RealtimeAssessment    GenerateDue   Digest Daybook Weekbook Monthbook
  Service                  Service               DigestsAsync   :1169  :973    :798     :609
  :258 alert               :214 assessment       (same as ──────┘
  :343 baseline            ▲                      digest col.)
        ▲                  │                            ▲
        │                  │                            │
   IDistributedCache   SSA gate:                   ≥1h since last
   (cache hit is       deviation ≥                 (≥2h early-day),
    the common case)   SampleJumpScore             night-gated
```

Of the 435 hits/day above, 278 return 200 and 141 are the §2 429 admission-reject (rejected at 0s, never queued). The remaining 16/day are 504 — Cloud Run's gateway timeout, a distinct failure mode from the 429: a request that ran past the 300s request timeout (§1) rather than being turned away on arrival. p95 inference sits at 216.3s (§1), close enough to that 300s ceiling that the heaviest calls plausibly cross it, though this hasn't been confirmed against logs. Either way, §1's "Refused requests (4xx)" tracks the 429s only, and the 42%-busy inference-time math in §2 doesn't fold the 504s' inference time in either — both undercounts would need the 504s added to be exact.

**Frequency per CardiMember per day** — the ceiling each gate permits:

| Call site | Gate | Per member/day |
|---|---|---|
| Digest narrative (`:1169`) | ≥1h since last, ≥2h early-day, night-gated | ≤ 16 |
| Dashboard status (`:258`, `:343`) | cache miss on a caregiver request | ~ 4 |
| Assessment (`:214`) | SSA deviation ≥ `SampleJumpScore` (~0.8% of windows pass) | ~ 2 |
| Daybook (`:973`) | one per day, unique-indexed | 1 |
| Weekbook (`:798`) | one per week, unique-indexed | 0.14 |
| Monthbook (`:609`) | one per month, unique-indexed | 0.03 |
| **Ceiling** | | **≈ 23** |

Note what the two triggers do to the digest column: `GenerateDueDigestsAsync` is called by **both** the `*/30` digest job and the `*/5` assess job, so it is *attempted* 336 times a day and the per-member 1-hour interval is the only thing standing between that and 336 calls.

**The observed rate does not reconcile with the ceiling.** Three members against a ceiling of ~23 each is ~69 calls/day; the service actually took **435**. Retries account for part of it — a failed call was three attempts — and the Weekbook and Monthbook passes were added on 08-18/08-19 and would have backfilled prior periods on first run. Neither has been confirmed as the full explanation, and until it is, no cadence change should be assumed to have the effect its interval implies (MS-6).

## 3. Why this shape is the expensive one

The workload is GPU-shaped and is running on CPU. That costs twice:

- **Latency** — a Q4 4B model generates at roughly 5–10 tok/s on 4 vCPU. The same GGUF on an L4 runs an order of magnitude faster.
- **Billing** — because each call takes ~124s, there is no idle window for scale-to-zero to exploit, so `min_instances = 1` is forced and the estate pays 86,400 s/day. The `cloud_run.tf` comment reaches this conclusion correctly; it is a consequence of the compute choice, not an independent constraint.

Faster inference removes the reason for the always-warm instance. That is the lever.

## 4. Options

Cost figures are **list-price arithmetic, not read off the bill** — the Cloud Billing API is disabled on project `carditrack-490120`, so actual spend could not be verified (§6, MS-1). Treat the ratios as sound and the absolute numbers as ±30%.

| Option | Region | Est. cost/mo | p50 | 429s | Notes |
|---|---|---|---|---|---|
| **A. Status quo** (Cloud Run CPU) | europe-west2 | ~$300–350 | 124s | ~14% | Baseline |
| **B. Cloud Run Job + L4, batched** | europe-west1/4 | **~$40** | ~10s | none | Cheapest and simplest; **leaves europe-west2** |
| **C. GKE Autopilot L4** | europe-west2 | ~$150–250 | ~10s | none | Keeps residency; adds a cluster and its fee |
| **D. Compute Engine Spot L4** | europe-west2 | ~$170–200 | ~10s | none | Keeps residency; preemptible, adds a VM to run |
| **E. Cut demand only** | europe-west2 | ~$300–350 | 124s | reduced | No migration; treats the symptom |

**Option B in detail.** Split by who is waiting. Per the measured 24h split, ~97% of calls are background digest regeneration and only ~13/day are the caregiver-facing Dashboard status line. Move the background work to a **Cloud Run Job with one L4**, triggered on a schedule, processing all due members within one instance lifetime; serve the Dashboard line from the last batch output rather than generating inside the request. Billed GPU time falls to roughly 3,400 s/day against 86,400 s/day today, running the same container and the same GGUF with one L4 attached (a Cloud Run deploy-time setting — `--gpu`/`--gpu-type` on `gcloud run deploy`, or the equivalent Terraform block; nothing about the Ollama invocation changes).

The Dashboard status line already covers the alert (`:258`) and baseline (`:343`) routes, both generated on a cache miss rather than on a cadence (§2a) — a newly-raised alert between batch passes has no fresh explanation to serve until the next one runs (MS-7).

### The region constraint

**Cloud Run's managed GPU is not offered in `europe-west2`.** L4 is available in `europe-west1`, `europe-west4`, `us-central1`, `us-east4`, `asia-southeast1`, and `asia-south1` (invitation only). L4 on Cloud Run also requires a minimum of 4 vCPU / 16 GiB — which the service already has.

The hardware itself is not the problem. Querying Compute Engine in `europe-west2` directly:

| Zone | Accelerators available |
|---|---|
| `europe-west2-a` | `nvidia-l4`, `nvidia-tesla-t4`, `tpu7x` |
| `europe-west2-b` | `nvidia-l4`, `nvidia-tesla-t4`, `nvidia-h100-80gb`, `nvidia-rtx-pro-6000` |
| `europe-west2-c` | `nvidia-rtx-pro-6000` |

So L4 is present in-region for Compute Engine and GKE — only the *managed Cloud Run GPU product* is absent. Options C and D therefore keep inference inside `europe-west2`; option B does not.

This is a compliance decision, not only a cost one. The DPIA pins hosting to `europe-west2`, records a US transfer surface as an open item (OI-5), and treats prompts as special-category health data. `europe-west1`/`europe-west4` remain within EU adequacy and are defensible; a US region would be a regression and is excluded.

**Resolved 2026-08-19 (owner):** moving to another region is acceptable. **Option B is therefore the recommendation** (§7).

### Only MedGemma has to move

The offer on the table was to relocate the whole estate. That is not necessary, and the smaller move is strictly safer.

MedGemma's callers no longer reach it through the VPC. `networking.tf` records the change: *"The original reason was ALL_TRAFFIC-egress callers reaching MedGemma's internal-ingress `*.run.app` URL through the VPC; those callers are now PRIVATE_RANGES_ONLY and authenticate to MedGemma by IAM instead, so that path is gone."* MedGemma runs `INGRESS_TRAFFIC_ALL` with IAM-authorised invokers, and `api`/`pipeline-jobs`/`pipeline-assessor` call it over its `*.run.app` URL, read from the `carditrack-dev-medgemma-service-url` secret.

So a cross-region MedGemma is a region string on one Cloud Run service plus the URL secret that already exists to carry it. No new subnet, no VPC peering, no data movement. The added latency is a cross-region hop of ~10ms against a call that currently takes 124s.

Relocating the rest of the estate would instead mean migrating Cloud SQL (with its data), Redis, GCS buckets, the load balancer and Cloud Armor, and re-opening the DPIA's residency position and the Auth0 tenant-region item (DPIA OI-4) — for no benefit MedGemma alone does not already obtain. If the estate moves later it should be for its own reasons, on its own change.

Two things to carry into whichever region is chosen: the Cloud SQL Auth Proxy still depends on `private_ip_google_access` on the existing subnet (dev's instance has no public IP), and Cloud NAT is currently *"a fixed ~£24/month charge for an unused gateway"* per its own Terraform comment — worth closing out in the same pass.

## 5. Alternatives considered

| Option | Outcome |
|---|---|
| Raise `medgemma_max_instances` above 1 | Rejected as the primary fix — multiplies the largest line item on the estate and inherits the "Ollama cannot safely multi-instance" concern. It buys capacity without addressing why a call costs 124s. |
| Drop to 2 vCPU / 8 GiB | Already refuted by measurement: 7.2 GiB p99 with a 9 GiB peak would OOM, and >8 GiB requires 4 vCPU regardless. |
| `min_instances = 0` on the current CPU service | Refuted at today's arrival rate — roughly one call every five minutes leaves no idle window, so it lands near the same 86,400 s/day while adding cold-start latency to the caregiver path. Becomes viable *only* once inference is fast enough to create idle windows, which is what a GPU does. |
| Swap Ollama for vLLM | Deferred, not rejected — and the case for it is **stronger** than `llm_design.md` assumed. Sampled completion logs show a median of **513 input tokens against 18 output tokens**: this workload is almost entirely prompt evaluation, not generation. That is precisely what `--enable-prefix-caching` eliminates, and precisely what llama.cpp cannot avoid under Gemma 3's sliding-window attention (`cached n_tokens = 0` on every generation). Against a shared fixed instruction block, the saving is on the dominant cost, not a marginal one. Two caveats: the sample is 8 short calls (MS-3), and vLLM needs HuggingFace weights under Health AI Developer Foundations terms — a licensing dependency, not a container swap. vLLM is also a GPU-only proposition; it is not an improvement on CPU. Sequence it after the compute move, not instead of it. |
| A smaller or more aggressively quantised model | Excluded by standing decision: dev runs the real model so that an assessment made in dev means something. |
| Vertex AI online endpoint | Rejected — always-on GPU billing, strictly worse than any batched option here. |

## 6. Open items

Numbered `MS-n` rather than `OI-n`: the DPIA maintains its own `OI-n` sequence. Any `OI-n` reference in this document means the DPIA's; none of MS-1..MS-7 resolves one — MS-2 needs the DPIA's hosting description (§4.3) updated directly, since DPIA OI-5 covers only US-linked transfer mechanisms and an EU-to-EU move doesn't touch it.

| ID | Item | Owner |
|---|---|---|
| MS-1 | No option in §4 is priced against actual spend. Two separate gaps — list prices and real cost — with different fixes; see §8. | Owner |
| MS-2 | ~~Residency decision~~ — **RESOLVED 2026-08-19 (owner): another region is acceptable.** Option B selected. Only the MedGemma service moves (§4). Still needs the DPIA's hosting description (§4.3) updated to the new region — not DPIA OI-5, which covers only US-linked transfer mechanisms and doesn't apply to an EU-to-EU move. | ~~Owner~~ + DPIA |
| MS-3 | The ~10s GPU p50 in §4 is an estimate from model size and quantisation, not a benchmark, and the 513-in/18-out token shape in §5 rests on **8 sampled calls** — biased toward short ones, because most hosts log the completion line below their ship level. Raise `Serilog__MinimumLevel__Default` on `pipeline-jobs` in dev for a day to get a real per-operation prompt/latency breakdown, then benchmark one batch on an L4. Both the GPU cost model and the vLLM case depend on this. | Engineering |
| MS-4 | Set `max_instance_request_concurrency` explicitly whatever else is decided. 640 on a single-instance inference service is a platform default nobody chose. | Engineering |
| MS-5 | The Cloud Run request timeout (300s) equals the client's `HttpClient.Timeout`, so client and server give up simultaneously and the loser is arbitrary. Separate them. | Engineering |
| MS-6 | Observed call volume (435/day) is ~6× the ceiling the per-member gates in §2a permit (~69/day). Retries and the new Weekbook/Monthbook backfills are the likely contributors but are unconfirmed. Reconcile before relying on any cadence interval to bound load. | Engineering |
| MS-7 | **Resolved 2026-08-21 (implemented): no on-demand fast path is needed for the status line.** The line is now a persisted `MemberStatusLine` row (`StatusLineGenerationService`) regenerated by the digest pass after every stored digest and by the assessor immediately after an alert is raised or resolved — so a freshly-raised heart-rate alert gets its fresh line seconds after the alert exists, in the same pass. The Worker's deterministic alerts (statistical, inactivity) cannot regenerate (no medical model there by design) and their line catches up on the next pipeline pass, minutes at most — acceptable for copy whose old cache TTL already tolerated fifteen. The on-demand alert (`:258`) and baseline (`:343`) *insight* endpoints stay request-path against MedGemma: ~13 calls/day, bounded by the 300s client budget. | ~~Owner~~ Resolved |

## 7. Recommendation

Do **MS-4 and MS-5 now** — they are small, independent of the compute decision, and remove two configurations nobody chose.

MS-2 is resolved: inference may leave `europe-west2`, so **take option B** — a Cloud Run Job with one L4 in `europe-west1` or `europe-west4`, batched, with the Dashboard status line served from the last batch output. Move **only the MedGemma service**; the rest of the estate has no reason to follow it (§4).

Do not treat option E as sufficient on its own. Cutting cadence lowers the refusal rate but leaves the estate paying 24h/day for 42% utilisation of the wrong kind of compute, and leaves p50 at two minutes on a path a caregiver waits on.

MS-1 does not block starting. It bounds how confidently the saving can be *stated*, not whether the move is right: option B is better than the status quo on latency, refusal rate and billed seconds regardless of what the exact rates turn out to be.

## 8. Achieving MS-1 — pricing this against reality

Two different gaps, often conflated. They have different fixes and different lead times.

**List prices** — exact per-second rates for Cloud Run vCPU/GiB in the target region and for the L4. Needs `cloudbilling.googleapis.com` enabled (currently `DISABLED`), then the Catalog API: `GET /v1/services` to find the Cloud Run service ID, `GET /v1/services/{id}/skus` for the rates. Follow the existing pattern in `deployments/apis.tf` — one `google_project_service` block with `disable_on_destroy = false` — rather than an imperative `gcloud services enable`. This turns the ±30% in §4 into exact list arithmetic, and is the cheaper half by far.

**Actual spend** — what the estate is really billed. There is **no REST API for this**; the only programmatic source is the Cloud Billing → BigQuery export. Note three things before relying on it:

1. The project currently has **no BigQuery datasets at all**, so the dataset is part of the work.
2. Configuring the export is a **billing-account** operation (`roles/billing.admin`), not a project one. The `carditrack-investigator` service account almost certainly lacks it; reading the result additionally needs `roles/billing.viewer` on the billing account.
3. **The export does not backfill.** Data flows from the moment it is configured. It cannot answer what MedGemma cost over the 08-09 → 08-19 window this document analyses.

For that history, the **Cost Table export (CSV) from the billing console** does cover past periods and needs no infrastructure — a human with billing access, a few minutes, filtered to the `carditrack-dev-medgemma` service. That is the fastest route to closing MS-1 for the window in §1, and the right first step.

Cost attribution afterwards depends on labels: the Cloud Run services take `var.cloud_run_labels`, and whatever is set there is what a BigQuery or Cost Table slice can group by. Confirm it distinguishes MedGemma from the other services before trusting a per-service figure.
