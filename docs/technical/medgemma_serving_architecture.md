# MedGemma serving architecture — CPU Cloud Run, and what replaces it

**Status:** Proposed (2026-08-19) — decision required, see §6
**Scope:** Where MedGemma inference runs and what it costs. Covers the Cloud Run CPU service as deployed, the measured failure mode (HTTP 429), and the GPU options. Does **not** cover which model is served, prompt content, or the SSA contract.
**Relationship to other docs:** [llm_design.md](../llm_design.md) owns the SSA → MedGemma contract and first sketched the GPU option. [apm_setup_runbook.md](./apm_setup_runbook.md) owns the client-side telemetry these numbers come from. [dpia.md](../compliance/dpia.md) owns residency and the transfer surface (OI-5). The `medgemma_min_instances` comment in [cloud_run.tf](../../infrastructure/deployments/cloud_run.tf) owns the warm-instance economics and stays correct — this document changes the compute underneath it, not that reasoning.

---

## 1. Context — what is actually deployed

`carditrack-dev-medgemma` serves `hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M` (4-bit quantised, ~3 GB) on **stock Ollama** — `ollama/ollama:latest`, `ENTRYPOINT ["ollama", "serve"]` ([Dockerfile](../../src/Infrastructure/MedGemma/Dockerfile)). Not vLLM; vLLM appears in this repo only as a future option in `llm_design.md`.

Read off the live service on 2026-08-19:

| Setting | Value | Source |
|---|---|---|
| CPU / memory | 4 vCPU / 16 GiB, `cpu_idle = false` | Terraform + live revision |
| Scaling | `min = 1`, `max = 1` | `medgemma_max_instances`, default 1 |
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

## 3. Why this shape is the expensive one

The workload is GPU-shaped and is running on CPU. That costs twice:

- **Latency** — a Q4 4B model generates at roughly 5–10 tok/s on 4 vCPU. The same GGUF on an L4 runs an order of magnitude faster.
- **Billing** — because each call takes ~124s, there is no idle window for scale-to-zero to exploit, so `min_instances = 1` is forced and the estate pays 86,400 s/day. The `cloud_run.tf` comment reaches this conclusion correctly; it is a consequence of the compute choice, not an independent constraint.

Faster inference removes the reason for the always-warm instance. That is the lever.

## 4. Options

Cost figures are **list-price arithmetic, not read off the bill** — the Cloud Billing API is disabled on project `carditrack-490120`, so actual spend could not be verified (§6, OI-1). Treat the ratios as sound and the absolute numbers as ±30%.

| Option | Region | Est. cost/mo | p50 | 429s | Notes |
|---|---|---|---|---|---|
| **A. Status quo** (Cloud Run CPU) | europe-west2 | ~$300–350 | 124s | ~14% | Baseline |
| **B. Cloud Run Job + L4, batched** | europe-west1/4 | **~$40** | ~10s | none | Cheapest and simplest; **leaves europe-west2** |
| **C. GKE Autopilot L4** | europe-west2 | ~$150–250 | ~10s | none | Keeps residency; adds a cluster and its fee |
| **D. Compute Engine Spot L4** | europe-west2 | ~$170–200 | ~10s | none | Keeps residency; preemptible, adds a VM to run |
| **E. Cut demand only** | europe-west2 | ~$300–350 | 124s | reduced | No migration; treats the symptom |

**Option B in detail.** Split by who is waiting. Per the measured 24h split, ~95% of calls are background digest regeneration and only ~13/day are the caregiver-facing Dashboard status line. Move the background work to a **Cloud Run Job with one L4**, triggered on a schedule, processing all due members within one instance lifetime; serve the Dashboard line from the last batch output rather than generating inside the request. Billed GPU time falls to roughly 3,400 s/day against 86,400 s/day today — the same container and the same GGUF, with `--gpu 1`.

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

## 5. Alternatives considered

| Option | Outcome |
|---|---|
| Raise `medgemma_max_instances` above 1 | Rejected as the primary fix — multiplies the largest line item on the estate and inherits the "Ollama cannot safely multi-instance" concern. It buys capacity without addressing why a call costs 124s. |
| Drop to 2 vCPU / 8 GiB | Already refuted by measurement: 7.2 GiB p99 with a 9 GiB peak would OOM, and >8 GiB requires 4 vCPU regardless. |
| `min_instances = 0` on the current CPU service | Refuted at today's arrival rate — roughly one call every five minutes leaves no idle window, so it lands near the same 86,400 s/day while adding cold-start latency to the caregiver path. Becomes viable *only* once inference is fast enough to create idle windows, which is what a GPU does. |
| Swap Ollama for vLLM | Deferred, not rejected. `--enable-prefix-caching` would genuinely fix the "reprocess the prompt from token zero on every call" cost that llama.cpp cannot avoid under Gemma 3's sliding-window attention. But it needs HuggingFace weights under Health AI Developer Foundations terms — a licensing dependency, not a container swap. Worth doing after the compute move, not as part of it. |
| A smaller or more aggressively quantised model | Excluded by standing decision: dev runs the real model so that an assessment made in dev means something. |
| Vertex AI online endpoint | Rejected — always-on GPU billing, strictly worse than any batched option here. |

## 6. Open items

| ID | Item | Owner |
|---|---|---|
| OI-1 | **Cloud Billing API is disabled** on `carditrack-490120`, so no option in §4 is priced against actual spend. Enable it (read-only use) and re-cost before committing. | Owner |
| OI-2 | Residency decision: is moving MedGemma inference to `europe-west1`/`europe-west4` acceptable (option B), or must it stay in `europe-west2` (options C/D)? Feeds DPIA OI-5. | Owner + DPIA |
| OI-3 | The ~10s GPU p50 in §4 is an estimate from the model size and quantisation, not a benchmark. Measure one batch on an L4 before committing to the cost model. | Engineering |
| OI-4 | Set `max_instance_request_concurrency` explicitly whatever else is decided. 640 on a single-instance inference service is a platform default nobody chose. | Engineering |
| OI-5 | The Cloud Run request timeout (300s) equals the client's `HttpClient.Timeout`, so client and server give up simultaneously and the loser is arbitrary. Separate them. | Engineering |

## 7. Recommendation

Do **OI-4 and OI-5 now** — they are small, independent of the compute decision, and remove two configurations nobody chose.

Then resolve OI-2, because it selects the architecture. If inference may leave `europe-west2`, **option B** is materially the best on every axis measured here and should be taken. If it may not, **option C** is the next best and keeps residency intact.

Do not treat option E as sufficient on its own. Cutting cadence lowers the refusal rate but leaves the estate paying 24h/day for 42% utilisation of the wrong kind of compute, and leaves p50 at two minutes on a path a caregiver waits on.
