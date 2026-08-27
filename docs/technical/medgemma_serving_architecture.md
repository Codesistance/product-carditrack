# MedGemma serving architecture — CPU Cloud Run, and what replaces it

**Status:** **Implemented (2026-08-21)** — option B built and serving; see §9 for what was built, what deviated from this document, and the benchmark that closes MS-3. MS-1 (real spend) needs a week of billing and stays open.
**Scope:** Where MedGemma inference runs and what it costs. Covers the Cloud Run CPU service as deployed, the measured failure mode (HTTP 429), and the GPU options. Does **not** cover which model is served, prompt content, or the SSA contract.
**Relationship to other docs:** [llm_design.md](../llm_design.md) owns the SSA → MedGemma contract and first sketched the GPU option. [apm_setup_runbook.md](./apm_setup_runbook.md) owns the client-side telemetry these numbers come from. [dpia.md](../compliance/dpia.md) owns residency and the US transfer surface (OI-5). The warm-instance economics were owned by the `medgemma_min_instances` comment on the per-environment CPU service in `deployments/cloud_run.tf` — removed with that service on 2026-08-27 (see git history); the shared service's own scaling reasoning lives in [common/cloud_run.tf](../../infrastructure/common/cloud_run.tf).

---

## 1. Context — what is actually deployed

*(Historical snapshot: this section describes the per-environment CPU service as it ran before the §9 GPU move. That service and its Terraform sources — `medgemma_min_instances`, the `deployments/cloud_run.tf` service block, the dev.tfvars entries — were removed on 2026-08-27; the live shape is §9's `carditrack-common-medgemma`.)*

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
| MS-3 | **Resolved 2026-08-21 (measured): see §9.2.** The estimate was pessimistic by an order of magnitude on prompt evaluation — ~4,000 tok/s measured against ~25 tok/s on CPU. Original text: The ~10s GPU p50 in §4 is an estimate from model size and quantisation, not a benchmark, and the 513-in/18-out token shape in §5 rests on **8 sampled calls** — biased toward short ones, because most hosts log the completion line below their ship level. Raise `Serilog__MinimumLevel__Default` on `pipeline-jobs` in dev for a day to get a real per-operation prompt/latency breakdown, then benchmark one batch on an L4. Both the GPU cost model and the vLLM case depend on this. | Engineering |
| MS-4 | ~~Set `max_instance_request_concurrency` explicitly whatever else is decided.~~ **Resolved (implemented): `max_instance_request_concurrency = 1` in `deployments/cloud_run.tf`**, with the reasoning recorded inline — a fast, honest 429 beats several calls quietly slowing each other down. | ~~Engineering~~ Resolved |
| MS-5 | ~~The Cloud Run request timeout (300s) equals the client's `HttpClient.Timeout`...~~ **Resolved (implemented): the service timeout is derived as `medgemma_timeout_seconds + 60` in `deployments/cloud_run.tf`**, so the client always gives up first and the loser of a timeout race is no longer arbitrary. | ~~Engineering~~ Resolved |
| MS-6 | Observed call volume (435/day) is ~6× the ceiling the per-member gates in §2a permit (~69/day). Retries and the new Weekbook/Monthbook backfills are the likely contributors but are unconfirmed. Reconcile before relying on any cadence interval to bound load. | Engineering |
| MS-7 | **Resolved 2026-08-21 (implemented): no on-demand fast path is needed for the status line.** The line is now a persisted `MemberStatusLine` row (`StatusLineGenerationService`) regenerated by the digest pass after every stored digest and by the assessor immediately after an alert is raised or resolved — so a freshly-raised heart-rate alert gets its fresh line seconds after the alert exists, in the same pass. The Worker's deterministic alerts (statistical, inactivity) cannot regenerate (no medical model there by design) and their line catches up on the next pipeline pass, minutes at most — acceptable for copy whose old cache TTL already tolerated fifteen. The on-demand alert (`:258`) and baseline (`:343`) *insight* endpoints stay request-path against MedGemma: ~13 calls/day, bounded by the 300s client budget. | ~~Owner~~ Resolved |
| MS-8 | **Resolved 2026-08-21 (implemented): the 300s client/server timeout was itself a source of load, not just a symptom.** Datadog logs from `pipeline-jobs` (dev, 16:02-16:27 UTC) showed the mechanism: MS-4's `max_instance_request_concurrency = 1` keeps the one instance "busy" (from Cloud Run's admission-control point of view) for as long as an in-flight generation takes to *return a response* — but a client-side timeout only stops the client from waiting, it does not stop Ollama from finishing the generation. An abandoned call therefore still occupies the one concurrency slot for its full natural duration, so the next caller's fresh request queues or 429s behind work nobody is waiting for any more, and every abandonment compounds the backlog for the next call. Observed in that window: two calls timed out at 300s and then took 642-643s each to fail on retry; a later call took 166s to return successfully for a prompt that had earlier taken 25s server-side. This reads as a self-reinforcing pileup, not (only) CPU saturation, and plausibly explains why §1's p50/p95 kept climbing (15s → 124s → 216s) without prompt sizes changing. **Fix:** `medgemma_timeout_seconds` raised 300s → 900s (`infrastructure/variables.tf`) so a real generation — including one queued behind a still-finishing prior call — has room to return an honest answer instead of being abandoned and adding to the backlog. Does not touch the Dashboard hero card's fail-fast budget (`PrivateAiSettings.CurrentStatusBudgetSeconds`, 25s), which is deliberately unaffected — the mobile client gives up at 30s regardless of what the server does. **Not yet done:** confirm in Datadog after a day of dev traffic that 429/timeout counts on `pipeline-jobs` actually drop; if the pileup persists even without abandonment (i.e. from genuine cross-job overlap between the `*/30` digest schedule and the `2-59/5` assessor schedule), client-side serialisation (a Redis-backed mutex around every MedGemma call, since Redis is already provisioned) is the next lever, not a resource increase. | Engineering (verify) |
| MS-9 | **Resolved 2026-08-27 (implemented): the context window was never set, so Ollama served its own 4096-token default and long structured replies were cut mid-token.** Datadog logs from `pipeline-jobs` (dev, 07:32-08:17 UTC) showed three `DigestAiResponse` parse failures for one member, each reporting an error at `$.summary` at a byte position within two of the body length (5520/5522, 6205/6205, 5171/5173) — the signature of a reply that stops rather than one that is malformed. `OllamaGenerateRequest` carried no `options` at all, so neither `num_ctx` nor `num_predict` was sent, and one unconfigured window had to hold the day of readings, the questionnaire answers, the reply schema **and** the completion. **Fix:** both are now explicit and configurable — `AI:Private:ContextTokens` (8192, `medgemma_context_tokens`) and `AI:Private:MaxOutputTokens` (2048, `medgemma_max_output_tokens`) — and startup refuses a pair where the ceiling leaves no room for a prompt. Independently, `MedGemmaClient` now reads Ollama's `done_reason`: a structured call that stops at the budget fails as `truncated` naming both numbers, instead of reaching the deserializer and being reported as content that could not be parsed. Raising the window costs KV cache on a CPU-served model, which is why it is a variable and not a constant. **Not yet done:** confirm in Datadog that `DigestAiResponse` parse failures stop for the affected member — the same member also tripped the hallucination guard four times that morning (a summary crediting 4195 steps against 124 actual), which this does not address. | Resolved |

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

---

## 9. What was actually built (2026-08-21)

### 9.1 Shape, and where it deviates from §4

| | This document proposed | Built |
|---|---|---|
| Compute | Cloud Run **Job**, batched | Cloud Run **service**, `carditrack-common-medgemma` |
| Region | `europe-west1` or `europe-west4` | `europe-west1` |
| Accelerator | one L4 | one L4, `gpu_zonal_redundancy_disabled = true` |
| Scaling | — | `min = 0`, `max = 1`, concurrency 1 |
| Ownership | per-environment | **one service, every environment** — `infrastructure/common/` |

### 9.1a Two things verification found, and one of them is a caveat on the whole design

**Cold start is ~54 s, not "seconds".** §9.1's premise — repeated from the migration plan — was that an L4 loads the model fast enough for `min = 0` to be free. Measured on the live service: `loading model` at 21:25:50, `model loaded` at 21:26:44. **54 seconds**, against the CPU service's 58.6 s. The GPU barely helps, and on reflection should not have been expected to: loading is reading ~3 GB of quantised weights off disk into memory, which is IO, not arithmetic.

That is a real caveat rather than a footnote. `min = 0` means *every* idle period costs the next caller ~54 s, and the CPU service kept `min = 1` precisely to avoid that. It is fine for the batch passes, which are not waited on. It is **not** fine for the first chat question after a quiet spell, which is the exact failure the warm instance existed to prevent — and it is not visible in §9.2's numbers, because those were all measured against an already-warm instance.

Observed both ways on 2026-08-21: a chat send at 21:25 took ~90 s end to end, dominated by this load; the next at 21:51 answered inside the same minute.

The saving and the latency are therefore in direct tension, and `min = 0` is a choice for cheapness over the interactive path, not a free win. Three options were open: accept it (batch is unaffected; chat pays after idle), set `min = 1` and give up most of the saving, or keep the model resident another way. **The third was taken on 2026-08-22 — see §9.5.** MS-1's billing week should be read with it in place.

**Ingress is `INGRESS_TRAFFIC_ALL`, where the per-environment service was internal-only.** Not an oversight to leave unstated: the old service relied on the API's `vpc_access` egress to satisfy internal ingress, and that mechanism does not survive the move — the callers are in `europe-west2` with `PRIVATE_RANGES_ONLY` egress and the service is in `europe-west1` with no VPC attachment, so their calls reach it over the public endpoint.

So the medical model's endpoint is now reachable from the internet, and authorisation rests entirely on IAM. Unauthenticated traffic arrives and is refused — a burst of ten `Empty Authorization header value` rejections at 21:57 on the day it went live, which is what internet-facing endpoints receive. The controls that make that acceptable are the ones already in place: no `allUsers` binding, two named invoker service accounts, and the public-exposure alert that moved with the service. What is *gone* is the second, network-position layer. Recorded in the DPIA's §4.3 residency note rather than left implicit.

**A service, not a Job.** The consumers are per-environment .NET jobs with their own databases and their own schedules; a service leaves `MedGemmaClient`, the OIDC identity-token handler and the URL-secret plumbing exactly as they were, where a Job would have meant a new invocation path for each environment. The cost of that choice is Cloud Run's idle scale-in tail — an instance lingers after the last request — so real spend may land nearer **$70–120/mo than §4's $40**. Still several times under the ~$300 it replaced. MS-1's week of billing settles it.

**One shared instance.** §4 assumed a service per environment. Prod runs no MedGemma today, and a single `min = 0` instance costs nothing when idle, so one serves both. Cross-stack IAM is a list of constructed service-account emails in `common.tfvars` — the common root cannot read the environment stacks' state, so an entry is a promise rather than a lookup, and prod's go in only once its accounts exist.

### 9.2 The benchmark MS-3 asked for

Measured on the live L4 service, 2026-08-21, from real pipeline and chat traffic — not synthetic:

| | CPU (`europe-west2`, 4 vCPU) | **L4 (`europe-west1`)** | |
|---|---|---|---|
| Prompt evaluation | ~25 tok/s | **~4,000 tok/s** | ~160× |
| 2,292-token prompt, eval only | ~92 s | **0.57 s** | |
| Token generation | ~5 tok/s | **~72 tok/s** | ~14× |
| Full call, 2,301 in / 250 out | 128–139 s | **4.3 s** | |
| Full call, 491 in / 30 out | ~23 s | **0.66 s** | |

§4 estimated a ~10s GPU p50 from model size and quantisation. That was pessimistic by an order of magnitude on the half that mattered: **prompt evaluation, not generation, was the cost on CPU** — a 1,184-token clinical prompt spent 47.6 s before its first output token, which is why p50 tracked prompt length rather than reply length.

One number to read carefully: the first call after a cold start measured `prompt_eval` at 59 tok/s, not 4,000. That is the GPU warming, not steady state. Everything after it is the figure above.

### 9.3 Three things this document did not predict

**The service URL is not derivable.** §4 and the migration plan both assumed `https://<service>-<project number>.<region>.run.app`. This project issues the older form — `https://carditrack-common-medgemma-zhsd62wx5a-ew.a.run.app`, a per-project hash and an abbreviated region. A constructed URL resolved to nothing and would have been seeded into an environment's MedGemma URL secret looking entirely plausible. The environment stacks now take it as an explicit, validated variable, because they cannot read the common stack's state to ask.

**The old URL secret's fallback was armed.** Its seed was `try(local service uri, "https://medgemma-not-deployed-…")`, and `secret_data` is ForceNew — so destroying the local service would have written that placeholder as a *new latest version*, silently replacing the working GPU URL for every consumer. The comment above that resource is a post-mortem of the same failure from 2026-08-20; the teardown would have re-armed it one step on.

**`deletion_protection` cannot be cleared in the destroying apply.** The provider defaults it true and refuses a destroy while it is, and removing the resource removes the place the flag would be set. Learned the expensive way on the rewrite-service teardown (one failed apply, two extra PRs); paid up front here.

### 9.4 Still open

- **MS-1** — a week of billing against the $70–120 envelope. Cannot be hurried, and now has to be read with §9.5's warm-ups in it: they raise instance hours in exchange for the interactive latency §9.1a gave up, and whether that trade lands inside the envelope is the same week's question.
- **MS-6** — the 435/day vs ~69/day call-volume discrepancy is untouched. It mattered most when each call cost two minutes of a saturated instance; at 4 s a call it is now a cost question rather than a latency one, but it is still unreconciled.
- **Prod** — no `carditrack-prod-*` service accounts exist, so there is nothing to grant `run.invoker` to. Prod is not wired to this service and does not run MedGemma at all.
- **The public-exposure alert** moved to `infrastructure/common/alerting.tf` with the service. Worth knowing the exposure it watches is now *larger*: a public grant on one instance is a public grant on the model behind every environment.

### 9.5 Warming the model when the app opens (2026-08-22)

§9.1a's caveat has one property worth exploiting: the ~54 s load does not have to happen *while someone waits for it*, only *before they need it*. A caregiver who opens the app does not ask the assistant a question in the same second — they land on the dashboard, read the status line and the digest, and only then open chat. That gap is longer than the load.

So the app's arrival at the dashboard now tells the API a caregiver is here, and the API starts the load in the background:

| | |
|---|---|
| Trigger | `PostLoginRouter`, on every route that lands on `AppShell` — cold launch, sign-in, account setup, verify-email. Not the onboarding wizard routes: no member yet, nothing to ask about |
| Call | `POST api/v1/assistant/prepare`, authenticated, fire-and-forget on the app side, `202` on the server's side within microseconds |
| Work | `MedGemmaWarmUpService` runs `MedGemmaClient.WarmUpAsync` on a detached task — Ollama's documented preload, a `/api/generate` with an **empty prompt**, which loads the weights and generates nothing |
| Guards | One warm-up at a time per host; no second one for `AI:Private:WarmUpMinimumIntervalSeconds` (default 300) after an attempt **ends**, success or failure alike; and **one attempt, no retries** — MS-4's `max_instance_request_concurrency = 1` means a warm-up that re-sent a 429 would be queueing ahead of the real calls it exists to speed up, and a 429 has already answered its only question |
| Off switch | `AI__Private__WarmUpEnabled=false` |

Three things this deliberately is not.

**It is not `min = 1` by the back door.** The instance comes up when a caregiver actually arrives and scales back to zero on Cloud Run's usual idle tail, so the estate pays for the hours people use the app rather than for all of them. The debounce is what keeps a morning's worth of app-opens from being a morning's worth of loads: at most one per host per five minutes, however many arrivals ask.

**It carries no health data and costs no tokens.** The empty prompt is the entire request. Nothing is generated, `prompt_eval_count` and `eval_count` come back absent, and the DPIA invariant `MedGemmaClient` opens with is not engaged because there is no content in either direction. The call is instrumented like any other — `gen_ai.operation.name = warm_up` on the shared span and duration metric — which is also how the real cost of this decision becomes visible in MS-1's billing week rather than inferred.

**It does not make anything wait, and is allowed to fail.** The endpoint answers before the load starts; the app never reads the answer; a failed warm-up logs a warning and starts the same five-minute clock a successful one does, so a model host that is down is dialled every few minutes rather than on every launch. If none of it happens, the first chat question pays the load exactly as it did before.

What is **not** covered: resuming the app from the background. `App.Resumed` is the other moment a caregiver arrives after a quiet spell, and it is the same one line to add — left out here because it multiplies how much of the day the instance is up, and that is a call to make against MS-1's numbers rather than ahead of them.
