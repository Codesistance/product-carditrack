# Vertex AI setup — ZDR configuration, region verification, model swaps

Status: Active (2026-08-21). Companion to the `AI:Public` / `AI:Rewrite` VertexGemini kinds
(`AiServiceExtensions`, `VertexAiClient`) and the compliance record in
[data_protection_architecture.md](data_protection_architecture.md) §9 (D6) and
[dpia.md](../compliance/dpia.md) v0.11 (A20, R-A4/M4).

## 1. What the code guarantees, and what it can't

The client (`VertexAiClient`) and its wiring guarantee: EU **regional** endpoint only (the host is
derived from `Location`; the Terraform variables validate against `europe-west2`/`west1`/`west4`
and the global endpoint is unreachable by construction), IAM auth via ADC (no API key), and the
MedGemmaClient-grade telemetry invariant (no prompt/completion text in any log, span, metric or
exception).

What code **cannot** guarantee is Google's server-side handling. Three project-level facts must be
configured and evidenced manually — they are the zero-data-retention (ZDR) posture the DPIA and
the §9 register cite:

## 2. ZDR configuration (do before the dev flip; evidence the output here or in the PR)

1. **Disable data caching** (default: cached up to 24 h in the serving data centre). Per region in
   use:

   ```bash
   TOKEN=$(gcloud auth print-access-token)
   for LOC in europe-west2 europe-west1 europe-west4; do
     curl -s -X PATCH \
       -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
       "https://$LOC-aiplatform.googleapis.com/v1/projects/carditrack-490120/locations/$LOC/cacheConfig" \
       -d '{"name": "projects/carditrack-490120/locations/'$LOC'/cacheConfig", "disableCache": true}'
   done
   ```

   Verify with a `GET` on the same resource: `disableCache: true`.

2. **Abuse-monitoring prompt logging opt-out.** Google may log prompts for abuse monitoring for
   customers **without** invoiced billing. Either confirm the billing account is invoiced (then no
   action), or file the [abuse-monitoring exception request](https://cloud.google.com/vertex-ai/generative-ai/docs/data-governance)
   for the project. Record which applies and when.

3. **No-training terms.** Google's Vertex AI data-governance commitment (customer data not used to
   train foundation models) applies by default under the Cloud Data Processing Addendum — confirm
   the current text at the link above and note the date checked. This is what backs the privacy
   policy's "we do not use it to train AI models" promise for this processor.

## 3. Model/region availability — verify before changing model or location

Model availability per EU region changes over time and is the one thing this integration cannot
assume. Before the first flip, and before any model or location tfvar change, run one probe per
candidate (needs `roles/aiplatform.user` and the API enabled):

```bash
LOC=europe-west2 MODEL=gemini-2.5-flash-lite
curl -s -X POST \
  -H "Authorization: Bearer $(gcloud auth print-access-token)" -H "Content-Type: application/json" \
  "https://$LOC-aiplatform.googleapis.com/v1/projects/carditrack-490120/locations/$LOC/publishers/google/models/$MODEL:generateContent" \
  -d '{"contents":[{"role":"user","parts":[{"text":"Reply with the word ok as JSON."}]}],
       "generationConfig":{"maxOutputTokens":64,"thinkingConfig":{"thinkingBudget":0},
         "responseMimeType":"application/json",
         "responseJsonSchema":{"type":"object","properties":{"reply":{"type":"string"},
           "n":{"type":["integer","null"]}},"required":["reply"]}}}'
```

This single probe verifies the three assumptions the client makes: the model is served from that
regional endpoint (404 = not there — try the next region in the allowlist), `responseJsonSchema`
is accepted **including the `["integer","null"]` type union** our schema exporter emits (400
naming the field = fall back to `responseSchema` + a nullable transform, which is a code change),
and `thinkingBudget: 0` is accepted (Flash tier does; Pro tier rejects it and is not a valid
target for these slots).

Region preference: `europe-west2` first (matches the estate and the DPIA's pin), then
`europe-west1`, then `europe-west4` — all inside the Terraform validation allowlist. Never
`global` and never a US region (DPIA exclusion).

## 4. Changing models — the whole point of the design

The model is a tfvar, not code:

- Rewrite slot: `rewrite_ai_model` (and `rewrite_ai_location` if the new model needs a different
  EU region) in `infrastructure/environments/<env>.tfvars` → apply → new revisions pick it up.
- Public slot: `public_ai_model` / `public_ai_location`, same flow.
- Local evaluation of a candidate rewrite model: point `tools/AiSplitEvaluator` at Vertex by
  setting `AI:Rewrite:Kind=VertexGemini` + `ProjectId`/`Location`/`Model` in its appsettings and
  running with developer ADC (`gcloud auth application-default login`).

Constraints a candidate must satisfy: served from an allowlisted EU region (§3 probe), accepts
`thinkingBudget: 0` and `responseJsonSchema`, and answers the rewrite register acceptably (the
evaluator compares candidates on the real prompts).

## 5. Auth and IAM

- Runtime: the api and pipeline service accounts carry `roles/aiplatform.user`
  (`deployments/service_accounts.tf`); tokens are minted by `VertexAccessTokenHandler` from ADC
  with the `cloud-platform` scope. No API key exists for this path — there is nothing to rotate
  or leak in Secret Manager.
- The API surface is enabled by `google_project_service.aiplatform` (`deployments/apis.tf`).
- 403 `SERVICE_DISABLED` = the API enablement hasn't applied; 403 `PERMISSION_DENIED` on the
  model = the SA lacks `aiplatform.user`; 429 = per-minute quota (the client backs off 15 s/30 s
  and honours `Retry-After` — sustained 429s mean the quota needs raising in the console, not a
  code change).
