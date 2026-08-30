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

## 2. ZDR configuration — COMPLETE: cache disable DONE (2026-08-21); abuse-monitoring exception APPROVED (2026-08-28)

These steps require `aiplatform.cacheConfigs.get/update` and billing-account visibility, which
no automation identity in this project holds — they are owner actions, evidenced here as they
close.

1. **Disable data caching — ✅ DONE, owner-executed 2026-08-21.** (Default: cached up to 24 h in
   the serving data centre.) The cache config is **project-scoped on the global admin host**
   (this is the management API, not inference routing — inference stays on the regional
   endpoints):

   ```bash
   TOKEN=$(gcloud auth print-access-token)
   curl -s -X PATCH \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     "https://aiplatform.googleapis.com/v1beta1/projects/carditrack-490120/cacheConfig" \
     -d '{"name": "projects/carditrack-490120/cacheConfig", "disableCache": true}'
   ```

   **Evidence (2026-08-21):** the PATCH returned a completed operation
   (`projects/206164751924/.../cacheConfig/operations/1214710209797160960`, `"done": true`), and
   the verify `GET` on the same URL returned:

   ```json
   { "name": "projects/206164751924/cacheConfig", "disableCache": true }
   ```

   Re-run the `GET` to re-verify at any time. (Path note, measured 2026-08-21: the
   location-scoped `/v1/.../locations/{loc}/cacheConfig` path from an earlier draft of this doc
   does not exist — the project-scoped path above is the real one, and it needs
   `aiplatform.cacheConfigs.*`, which only owner identities hold.)

2. **Abuse-monitoring prompt logging opt-out — ✅ EXCEPTION APPROVED 2026-08-28.** The owner
   submitted the exception form on 2026-08-21 (project number `206164751924`, project
   `carditrack-490120`, contact `cloudoperations@codesistance.com`, GDPR/DPIA justification).

   **Evidence (2026-08-28):** approval email received at
   `cloudoperations@codesistance.com` from
   `google-cloud-trusted-tester-administrator@google.com`, subject *"[Codesistance]: Your
   request for the exception to the prompt logging is approved"*, body: "The Generative AI
   Services team has approved Codesistance for the exception to the prompt logging policy as
   outlined in the Abuse Monitoring documentation for Google Cloud project number(s)
   [206164751924]." With item 1's cache disable, the ZDR posture this section exists to
   evidence is now fully in place: cache disabled, prompts/responses/identifiable metadata
   cleared prior to abuse-monitoring logging, no-training terms per item 3.

   Original determination and route, kept for the record:
   Google may log prompts for abuse monitoring for customers **without** invoiced billing.
   **Determined 2026-08-21 (owner, billing console):** the billing account
   (`01D957-C56D9C-17BCCB`, Codesistance Ltd) is **self-serve** (Postpay, card-paid), not
   invoiced — so the project IS in scope for prompt logging until the exception is approved.
   Action: file the exception request via the form linked from
   [Abuse monitoring](https://cloud.google.com/vertex-ai/generative-ai/docs/learn/abuse-monitoring)
   ("customers may request an exception by filling out this form"), for project
   `carditrack-490120` / the billing account above. On approval, Google clears prompts,
   responses and identifiable metadata prior to logging. Record the filing date and the
   approval here when they happen. Until approval, the honest posture is: cache disabled (item
   1), prompt logging for abuse monitoring possible.

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

Measured availability, 2026-08-21 (publisher-model metadata `GET
/v1/publishers/google/models/{m}` per regional endpoint — 200 = served, 404 = not; a cheaper
signal than `generateContent`, which needs `aiplatform.user`):

| Model | europe-west2 | europe-west1 | europe-west4 |
|---|---|---|---|
| gemini-2.5-flash-lite | ✗ 404 | ✓ | ✓ |
| gemini-2.5-flash | ✓ | ✓ | ✓ |
| gemini-3.1-flash-lite | ✗ | ✗ | ✗ |
| gemini-3.5-flash | ✓ | ✗ | ✓ |
| gemini-3.5-flash-lite | ✗ | ✗ | ✗ |

`gemini-3.5-flash-lite` measured 2026-08-30 (publisher-model metadata probe), after the model
went out on 2026-08-25 without this probe having run and 404'd in Dev's `pipeline-jobs` for
hours (`env:dev`, HTTP 404 on `generate_structured`, ~11 errors/hour) — it is not served in any
allowlisted EU region, so no `rewrite_ai_location` value fixes it. `rewrite_ai_model` and
`rewrite_ai_location` are reverted to `gemini-3.5-flash` / `europe-west2`, the documented
fallback, confirmed served there (and in `europe-west4`; 404 in `europe-west1`, so it could not
just take over the old `europe-west1` default). Re-measure before any model change — availability
moves. The AiSplitEvaluator comparison against the real rewrite prompts still has not run for
either model.

### Retirement clock (checked 2026-08-25)

Availability has a second axis: Google retires Gemini models on a schedule, and a retired model
fails exactly like an unavailable one — after having worked for months.

| Model | Retirement | Consequence for us |
|---|---|---|
| gemini-2.0-flash (+ -lite, -001) | **Retired** — 2026-03-03 on Vertex, 2026-06-01 on the Gemini API | Was the `public_ai_model` fallback default; prod's public slot ran it via the API-key kind and had been calling a dead model since 2026-06-01. Fixed 2026-08-25: prod tfvars pin the Vertex flip, and the Terraform default is bumped so the dead model cannot come back. |
| gemini-2.5-flash / -flash-lite / -pro | **~2026-10-16** (release notes say the 16th, the lifecycle page the 20th — plan for the 16th) | Cleared in configuration 2026-08-25 (owner decision): the estate standardised on the 3.5 generation everywhere at once — public slot `gemini-3.5-flash` in both environments, rewrite slot `gemini-3.5-flash-lite` at the time (reverted 2026-08-30, see below) — rather than staging dev-first, since the interim 2.5 pins never shipped an apply and the deadline stood regardless. |
| gemini-3.1-flash-lite | n/a (skipped) | Was the intended rewrite-slot target but never reached an allowlisted EU region (matrix above); superseded by gemini-3.5-flash-lite. |
| gemini-3.5-flash-lite | n/a (ruled out 2026-08-30) | Shipped 2026-08-25 without the §3 probe; 404s in every allowlisted EU region (matrix above). Rewrite slot reverted to gemini-3.5-flash. |
| gemini-3.5-flash | current target, both slots | Availability measured (matrix above) and served in europe-west2/west4. Rewrite slot now pins europe-west2. The AiSplitEvaluator comparison against real rewrite prompts is still an open step, not yet a blocker since this is a same-tier fallback rather than a new candidate. |

Sources: the Gemini API deprecations page and the Vertex AI model lifecycle page — re-check both
whenever this table is consulted, and re-date the heading when re-checked.

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
