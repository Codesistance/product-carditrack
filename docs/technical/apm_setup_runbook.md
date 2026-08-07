# APM Setup Runbook (Operator)

Connects the deployed API, Web, and Worker to the APM backend (**Datadog** — selected per
environment by the `apm_engine` tfvar). The apps are already wired; the whole deployed
contract is two env vars per service:

- `Apm__Engine` — plaintext, set by Terraform (`"Datadog"`; `"BetterStack"` also supported)
- `Apm__Data` — Secret Manager-backed (secret `carditrack-<env>-apm-data`) holding one JSON
  object with the selected engine's connection details (per-engine shapes below)

Until the secret holds real JSON the apps run normally and ship nothing — the `REPLACE_ME`
placeholder counts as "not configured". Malformed JSON in the secret fails startup loudly.

Quota guardrails are enforced in code (`CardiTrack.Observability`), engine-independently:
only Warning+ logs ship, traces are head-sampled at 20% (DB commands included via Npgsql
spans), `/health(z)` is never traced, and metrics (runtime, ASP.NET Core, HttpClient,
Npgsql) ship only when the `apm_metrics_enabled` tfvar is true (→ `Apm__MetricsEnabled`
env var) — they bill as custom metrics, so the switch is off by default. Current
per-environment values: **dev `apm_metrics_enabled = true`, prod `= false`**.

**Known exception to the guardrails**: the API's `appsettings.json` overrides the
defaults with `Apm:MinimumLogLevel = "Information"` and `Apm:TracesSampleRatio = 1.0`
(100% sampling) — so the API ships far more than the Warning+/20% baseline wherever
APM is configured. This is flagged as a code follow-up; until it lands, this override
is the deployed reality.

## 1. Datadog console steps

1. Sign in (or create the org) with the cloud-ops account
   (cloudoperations@codesistance.com — not a personal account). The **site** is fixed at
   org creation (EU data residency → `datadoghq.eu`); note the site from the browser URL
   (e.g. `app.datadoghq.eu` → site `datadoghq.eu`, `us5.datadoghq.com` → `us5.datadoghq.com`).
   This becomes `IngestUrl`.
2. **Organization Settings → API Keys → New Key**, name `carditrack-<env>`. The key value
   becomes `IngestToken`. (API key, not Application key.)
3. Traces need the **agentless OTLP intake endpoint**. It follows the per-site pattern
   `https://otlp.<site>/v1/traces` (e.g. UK1 → `https://otlp.uk1.datadoghq.com/v1/traces`,
   EU → `https://otlp.datadoghq.eu/v1/traces`), but access is org-entitlement-gated: if
   sends return 403 "organization is not allowed", request access via **Help → Support**
   (or the CSM). The full URL becomes the `TraceEndpoint` field. Skipping it is fine —
   the apps ship logs only until it's set (and log a startup Warning saying so).
4. Metrics need no extra field: when `apm_metrics_enabled = true`, the intake URL is
   derived from the site (`https://otlp.<site>/v1/metrics`); an optional `MetricsEndpoint`
   field overrides it. The same org entitlement applies.

Datadog `Apm__Data` shape:

```json
{"IngestUrl":"datadoghq.eu","IngestToken":"<api key>","TraceEndpoint":"https://otlp.datadoghq.eu/v1/traces"}
```

<details>
<summary>Alternative engine: Better Stack source steps</summary>

1. Sign in at https://telemetry.betterstack.com with the cloud-ops account.
2. Sources → **Connect source**, platform **OpenTelemetry**, name it `carditrack-<env>`.
3. Note the **source token** (→ `IngestToken`) and **ingesting host**
   (e.g. `s123456.eu-nbg-2.betterstackdata.com` → `IngestUrl`). No extra fields.
4. Set `apm_engine = "BetterStack"` in the environment's tfvars and `terraform apply`.

</details>

## 2. Provision the secrets

Terraform has already created the placeholder secret (`carditrack-<env>-apm-data`) with
compute-SA read access; if this environment predates it, run `terraform apply` first
(also required to pick up an `apm_engine` change).

```bash
bash scripts/set-apm-secrets.sh dev   # prompts for URL + token (+ optional trace endpoint)
```

(Equivalent by hand:
`printf '{"IngestUrl":"datadoghq.eu","IngestToken":"...","TraceEndpoint":"https://..."}' | gcloud secrets versions add carditrack-dev-apm-data --project=carditrack-490120 --data-file=-`)

## 3. Roll out

Cloud Run resolves secret-backed env vars at instance start, so force new revisions:

```bash
gcloud run services update carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
gcloud run services update carditrack-dev-web --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
gcloud run services update carditrack-dev-worker --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
```

## 4. Verify (before blaming app code)

```bash
# Traces: sampled at ~20%, so send a burst; expect a handful in Datadog -> APM -> Traces
# (requires TraceEndpoint in the secret; logs-only setups skip this check)
for i in $(seq 20); do curl -s -o /dev/null https://api.dev.carditrack.com/api/does-not-exist; done

# Logs: check Datadog -> Logs -> Live Tail. A quiet healthy app ships NOTHING
# (only Warning+) — absence of logs is not a fault.
# Startup self-report: each service logs its effective APM state at boot — look in
# Cloud Run logs for "APM configured: engine Datadog shipping logs+traces+metrics ..."
# (Information) or "APM shipping disabled: ..." / "APM (Datadog): traces will not
# ship: ..." (Warning) naming exactly what is missing.
# Check the env vars actually reached the revision:
gcloud run services describe carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --format=json | grep -A3 Apm__   # Apm__Engine plaintext + Apm__Data referencing the apm-data secret
# Startup crash-looping after rollout => bad Apm__Engine value ("Unknown APM engine" in
# Cloud Run logs) or malformed JSON in the apm-data secret ("not valid JSON").
```

## 5. Mobile app monitoring

The MAUI app ships crashes, sessions, and API request timings via the same generic
Engine + Data contract as the server, stamped into builds by CI:

- `carditrack-<env>-apm-mobile-engine` — **Terraform-owned**: the `apm_mobile_engine`
  tfvar (`"Datadog"` today). Switching mobile engines = flip the tfvar + `terraform apply`.
- `carditrack-<env>-apm-mobile-data` — operator-filled JSON with the engine's client-side
  details. **Embed-safe identifiers only** (they end up inside the app binary); never put
  runtime secrets here.

For Datadog (RUM; Android/iOS only, Session Replay deliberately disabled — health data
must not be screen-recorded):

1. In the same Datadog org: **Digital Experience → Real User Monitoring → Add Application**,
   name `carditrack-mobile`, platform **Android** (the MAUI SDK reports both platforms into
   one application). Note the **Application ID** and the generated **Client Token** (both
   write-only identifiers, safe to embed).
2. Populate the data secret:

```bash
printf '%s' '{"ClientToken":"<pub...>","ApplicationId":"<uuid>","Site":"Eu1"}' \
  | gcloud secrets versions add carditrack-dev-apm-mobile-data --project=carditrack-490120 --data-file=-
```

3. The next mobile CI build stamps engine + data in (`-p:ApmEngine=... -p:ApmData=<base64>`);
   placeholder data stamps as empty, which disables monitoring entirely — unprovisioned
   environments and local builds ship nothing. A bad engine name or malformed JSON logs
   and skips at app startup (monitoring must never brick the app).
4. Verify: install the internal-track build, open the app, then Datadog →
   **RUM → Sessions** (and force a crash in a test build for Error Tracking).

Notes: the Datadog SDK raised the Android minimum from API 21 to 23; `Site` defaults to
`Eu1` when omitted; consent is currently `Granted` at first launch — add a settings
toggle before any store review that requires opt-in analytics consent. RUM sessions are
**unsampled** (`SessionSampleRate = 100`) — fine at beta scale, revisit before broad
rollout. The app also sets `FirstPartyHosts` for the API host with Datadog + W3C
`traceparent` tracing headers, so RUM resource timings correlate with the API's OTel
traces (RUM→APM correlation).

## 6. Later / non-blocking

- Per-app keys/sources (separate tokens for API and Web) — split into per-app `apm-data`
  secrets if quota attribution ever matters; today all services share the one secret.
- Raise `Apm:TracesSampleRatio` / lower `Apm:MinimumLogLevel` via plaintext env vars
  (`Apm__TracesSampleRatio`, `Apm__MinimumLogLevel`) if the plan is upgraded; flip
  `apm_metrics_enabled` per environment in tfvars to turn metrics on or off.
- Switching backends: implement `IApmProvider`, register it in `ApmProviderRegistry`,
  flip `apm_engine` in the environment's tfvars (`infrastructure/environments/<env>.tfvars`),
  and put the new backend's JSON in the same `apm-data` secret — extra fields beyond
  IngestUrl/IngestToken are surfaced to the provider via `Data.Extra`.
