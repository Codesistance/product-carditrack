# APM Setup Runbook (Operator)

Connects the deployed API, Web, Worker, and PipelineJobs to the APM backend (**Datadog**
— selected per environment by the `apm_engine` tfvar); note PipelineJobs receives the
same env vars but is a Cloud Run **job**, not a service (rollout note in §3). The apps
are already wired. Two env vars per
service carry the **connection**; the volume knobs (`Apm__MetricsEnabled`,
`Apm__TracesSampleRatio`, `Serilog__MinimumLevel__Default`) are separate and documented
below:

- `Apm__Engine` — plaintext, set by Terraform (`"Datadog"`; `"BetterStack"` also supported)
- `Apm__Data` — Secret Manager-backed (secret `carditrack-<env>-apm-data`) holding one JSON
  object with the selected engine's connection details (per-engine shapes below)

Until the secret holds real JSON the apps run normally and ship nothing — the `REPLACE_ME`
placeholder counts as "not configured". Malformed JSON in the secret fails startup loudly.

Quota guardrails are enforced engine-independently: logs ship at the service's Serilog
root level (`Warning` by default — dev deliberately runs the API and Worker at
`Information`), `/health(z)` is never traced, and metrics (runtime, ASP.NET Core, HttpClient, Npgsql,
GenAI) ship only when the `apm_metrics_enabled` tfvar is true (→ `Apm__MetricsEnabled` env var)
— they bill as custom metrics, so the switch is off by default. Current per-environment
values: **dev `apm_metrics_enabled = true`, prod `= false`**.

All services carry the **same** volume settings in `appsettings.json` —
`Serilog:MinimumLevel:Default` `Warning` and `Apm:TracesSampleRatio` `1.0` (full
sampling, DB commands included via Npgsql spans). `Apm:MinimumLogLevel` is **not** set
in any `appsettings.json` — the Datadog sink inherits the Serilog root level unless it
is explicitly pinned (below). Terraform then sets two of those per service, so one
service can be tuned without touching the others (prod's tfvars list every service at
the baseline; dev deliberately runs `api` and `worker` at `Information` for
wearable-sync/OAuth diagnosis — dev is *not* at baseline):

| tfvar (object, one attribute per service) | env var | baseline |
| --- | --- | --- |
| `log_minimum_level` | `Serilog__MinimumLevel__Default` | `Warning` |
| `traces_sample_ratio` | `Apm__TracesSampleRatio` | `1.0` |

Every attribute is optional, so `log_minimum_level = { api = "Debug" }` turns the API up
and leaves Web and Worker at `Warning`. Two things to know when you do that:

- The Datadog sink **inherits** the Serilog root level unless `Apm:MinimumLogLevel` is
  explicitly pinned (`ShipLevel = MinimumLogLevel ?? InheritedLogLevel ?? Warning`), so
  turning a service up raises what ships to Datadog too, not just Cloud Logging — treat
  it as an ingest-spend change. `Apm__MinimumLogLevel` is now only for holding the sink
  **stricter** than the root (e.g. root `Debug` for Cloud Logging while still shipping
  only `Warning`+).
- Values *below* `Information` need `Logging__LogLevel__Default` set to match: the
  Microsoft.Extensions.Logging filter runs ahead of Serilog and stays at `Information`
  in the API and Worker `appsettings.json`, so `Debug`/`Verbose` is dropped before
  Serilog sees it.

Trace sampling at 100% is the largest ingest lever after metrics — cut
`traces_sample_ratio` first if Datadog spend needs reducing. The code fallback, used
only when nothing configures the value at all, stays at `0.2`
(`ApmOptions.TracesSampleRatio`) so an unconfigured host fails cheap rather than at
full sampling.

### AI-call telemetry (`CardiTrack.Ai`)

Every MedGemma call the API makes is instrumented client-side (the Ollama container is
stock and cannot be) through the `CardiTrack.Ai` ActivitySource/Meter pair — defined in
`AiTelemetry` (CardiTrack.Infrastructure), registered by name in `ApmExtensions`, with
the shared string in `TelemetryNames` (CardiTrack.Shared).

- **Span** — one `generate_content <model>` client span per call under the request
  trace (the HttpClient POST nests beneath it), carrying GenAI semantic-convention
  tags: `gen_ai.operation.name`, `gen_ai.provider.name`, request/response model,
  `gen_ai.usage.input_tokens` / `output_tokens`, and `error.type` on failure. Subject
  to the same head-sampling as the rest of the trace.
- **Metrics** — `gen_ai.client.operation.duration` (seconds, buckets sized for a 300 s
  timeout and cold starts) and `gen_ai.client.token.usage` (split by
  `gen_ai.token.type` input/output), behind the same `apm_metrics_enabled` switch as
  every other meter.
- **Log line** — one Information completion log per call (model, elapsed ms, token
  counts, done_reason, Ollama server-side timings, trace id), enabled by the Serilog
  override `CardiTrack.Infrastructure.ExternalClients.Medical → Information` in the API
  `appsettings.json`. The APM sink still filters at the service's ship level, so where
  the root stays `Warning` (prod baseline) completion logs reach Cloud Logging but not
  Datadog — there the span and metrics carry the same fields. Failures log at Error and
  ship everywhere.
- **Privacy (DPIA)** — none of these signals ever carries prompt text or model output:
  token counts, durations, model names, status codes and JSON error positions only.
  `MedGemmaClientTests` pins that invariant; keep it green.

The MedGemma source is not the only custom instrumentation `ApmExtensions` registers:
the **Npgsql** source contributes the DB command spans, `TelemetryNames.PipelineSource`
emits one consumer span per pulled Pub/Sub message (span-linked to the publishing
webhook span), and `TelemetryNames.PushSource` emits one span per FCM send — needed
because FirebaseAdmin bypasses `IHttpClientFactory`, so no HttpClient span would exist
otherwise. Push delivery also carries its own `notification.*` meters (including
`notification.time_to_ack`), behind the same `apm_metrics_enabled` switch.

### How each app identifies itself

Each host reports as its **app type**, lowercase — `api`, `web`, `worker`,
`pipeline-jobs`, `webhook-receiver` (the mobile app is separate: `carditrack-mobile`,
set in `MobileApm`). All five hosts call `AddApmTracing`, but note the webhook receiver
is wired in code only — Terraform gives it no `Apm__Engine`/`Apm__Data` yet, so it runs
console-only for now. The service name is what the **Service** facet
filters on in Logs and APM, and both signals use the same string: the log sink's service
field and the OTel resource's `service.name` come from one constant per host
(`ApmServiceNames`), because Datadog joins a log to its trace on service — different
spellings correlate with nothing. The names are code, not config; changing one means
changing the constant and re-deploying, and any saved view or monitor filtering the old
value goes blank.

The running release is tagged `version:<semver>` on every log and set as `service.version`
on every span, both from `DeploymentInfo.Version` (the deploy's image tag, stamped in at
build time — see the `VERSION` build arg in each Dockerfile). It has to travel as a
Datadog *tag*: the reserved **Version** facet reads tags, so the `Version` log property
the hosts also enrich with is only an ordinary attribute to it. A build that was not
stamped reports `0.0.0-local`, and one whose version cannot be resolved at all reports
`unknown` — both mean "not a release", visibly.

The environment completes the `env` / `service` / `version` triple: logs carry an
`env:<name>` tag and spans carry `deployment.environment.name` (plus the older
`deployment.environment`, since OTLP intakes are mid-migration between the two keys).
Unlike the version it **cannot be baked into the image** — dev and prod deploy the *same*
image, promoted by tag — so it arrives at runtime from `ASPNETCORE_ENVIRONMENT`, which
Terraform already sets per environment as `title(var.environment)`. `DeploymentInfo`
lowercases it, so `Dev` ships as `env:dev` and `Prod` as `env:prod`; without that they
would be two environments, since tags are case-sensitive.

Two things follow from reading that variable:

- It is the same value that selects `appsettings.<env>.json`. Deployed hosts get `Dev` /
  `Prod`, **not** .NET's `Development` / `Production`, so `IsDevelopment()` is false
  everywhere and dev runs production-like config. Set `DEPLOY_ENVIRONMENT` to relabel
  telemetry without disturbing that.
- When neither variable is set, the environment is reported as **nothing at all** rather
  than defaulting to `Production` the way `IHostEnvironment` would. The `env` tag is then
  omitted and startup logs a Warning naming both variables. An unlabelled log is findable;
  one mislabelled `prod` is a false alarm at 3am.

## 1. Datadog console steps

1. Sign in (or create the org) with the cloud-ops account
   (cloudoperations@codesistance.com — not a personal account). The **site** is fixed at
   org creation; read it off the browser URL (e.g. `app.datadoghq.eu` → site
   `datadoghq.eu`, `us5.datadoghq.com` → `us5.datadoghq.com`). This becomes `IngestUrl`.
   **This org is on UK1 (`uk1.datadoghq.com`)** — every example below uses that site, and
   sending to the wrong one silently lands the data in an org nobody is watching.
2. **Organization Settings → API Keys → New Key**, name `carditrack-<env>`. The key value
   becomes `IngestToken`. (API key, not Application key.)
3. Traces need the **agentless OTLP intake endpoint**. It follows the per-site pattern
   `https://otlp.<site>/v1/traces` (e.g. UK1 → `https://otlp.uk1.datadoghq.com/v1/traces`,
   EU → `https://otlp.datadoghq.eu/v1/traces`), but access is org-entitlement-gated: if
   sends return 403 "organization is not allowed", check **Organization Settings → API
   Keys → OTLP Ingest** first, or request access via **Help → Support** (or the CSM) if
   that toggle isn't visible. The full URL becomes the `TraceEndpoint` field. Skipping it
   is fine — the apps ship logs only until it's set (and log a startup Warning saying so).
4. Logs and metrics need no extra field: the intake URLs are derived from the site
   (`https://otlp.<site>/v1/logs`, `https://otlp.<site>/v1/metrics`); optional
   `LogsEndpoint` / `MetricsEndpoint` fields override them (metrics only ships when
   `apm_metrics_enabled = true`; logs always ship once the engine is configured). Logs
   go through the **same agentless OTLP intake as traces**, not a separate always-on
   pipeline — the same org entitlement above applies, and it's *why* logs and traces
   correlate: sharing one intake means they share one OTel Resource
   (`service.name`/`service.version`/`deployment.environment`), which is what Datadog
   joins a log to its trace on.

Datadog `Apm__Data` shape:

```json
{"IngestUrl":"uk1.datadoghq.com","IngestToken":"<api key>","TraceEndpoint":"https://otlp.uk1.datadoghq.com/v1/traces"}
```

> **Log status normalisation** is app-side: `DatadogLogStatusEnricher` stamps the
> lowercase canonical level attribute onto Datadog-bound logs so the **status** facet
> resolves (`Fatal` maps to `emergency`). The documented fallback is a clone-and-remap
> log pipeline, kept at `infrastructure/datadog/pipelines/otel-severity-to-status.json`.

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

# PipelineJobs is a Cloud Run JOB, not a service — update the job too (or skip it:
# each scheduled execution resolves the secret fresh at start, so the next run picks
# it up on its own):
gcloud run jobs update carditrack-dev-pipeline-jobs --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
```

## 4. Verify (before blaming app code)

```bash
# Traces: fully sampled (traces_sample_ratio 1.0 in both envs), so a single request
# should appear in Datadog -> APM -> Traces
# (requires TraceEndpoint in the secret; logs-only setups skip this check)
curl -s -o /dev/null https://api.dev.carditrack.com/api/does-not-exist

# Logs: check Datadog -> Logs -> Live Tail. A quiet healthy app at the Warning root
# baseline ships NOTHING (dev's API and Worker run at Information, so they chat more)
# — absence of logs is not a fault.
# Filter by app with service:api / service:web / service:worker, and confirm the
# release landed with the Version facet (or "version:<semver>" in the query bar).
# env:dev / env:prod separates the environments — mobile already reports the same two
# values. Logs with no env at all mean neither ASPNETCORE_ENVIRONMENT nor
# DEPLOY_ENVIRONMENT reached the revision; the startup Warning names both.

# Correlation (the actual point of shipping both): open one of the sampled traces from
# APM -> Traces and confirm its "Logs" tab shows matching lines for that request — or
# open a log line from the burst above and confirm "Related Trace" resolves. Logs and
# traces now ship through the same OTLP intake specifically so this works; if a trace
# shows zero related logs, check the log's env/service/version facets match the trace's
# exactly (a mismatch there is the correlation failing, not a missing log).
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

The MAUI app ships logs and traces via the same generic Engine + Data contract as the
server, stamped into builds by CI:

- `carditrack-<env>-apm-mobile-engine` — **Terraform-owned**: the `apm_mobile_engine`
  tfvar (`"Datadog"` today). Switching mobile engines = flip the tfvar + `terraform apply`.
- `carditrack-<env>-apm-mobile-data` — operator-filled JSON with the engine's client-side
  details. **Embed-safe identifiers only** (they end up inside the app binary); never put
  runtime secrets here.

For Datadog (Android/iOS only; Session Replay deliberately disabled — health data must not
be screen-recorded):

**Do not populate the mobile data secret — mobile monitoring is inert on this org.**
The org is on UK1, and an unnameable `Site` disables monitoring outright (details
below), so a client token buys nothing here. Leave `carditrack-<env>-apm-mobile-data`
at its `REPLACE_ME` placeholder: placeholder data stamps as empty at build time, which
disables monitoring cleanly (a bad engine name or malformed JSON likewise logs and
skips at app startup — monitoring must never brick the app). Mobile diagnostics come
from the **on-device Serilog log files** and **Play Console → Quality → Android
vitals** instead.

### `Site` must be one the SDK can name — UK1 cannot be reached

**This org is on UK1 (`uk1.datadoghq.com`), and the mobile SDK cannot ship to it.** The
`DatadogSite` enum in `Datadog.Maui` names only `Us1`, `Us3`, `Us5`, `Eu1`, `Ap1`, `Ap2`
and `Us1Fed` — and so does the native `dd-sdk-android-core` enum underneath it
(`us1/us3/us5/eu1/ap1/ap2` plus the gov sites, verified by extracting the 3.10.0 AAR).
There is no UK1 entry at any layer.

`CustomEndpoint` is **not** a workaround, despite being on the config surface for Logs,
Traces, RUM and Session Replay: `Datadog.Maui` 0.2.0 never calls the native
`useCustomEndpoint` for any feature (no such reference exists in the assembly), so every
feature targets the site-derived intake no matter what is configured. Only 0.2.0 has ever
been published, so no package bump fixes this.

Consequences, all confirmed on a device (2026-08-11):

- Setting `"Site":"Uk1"` **disables monitoring outright** — an unnameable site is fatal by
  design, so telemetry is never misdelivered to another region. The reason is logged at
  startup to the on-device Serilog file, readable from a Release build.
- RUM was removed for this reason: it only ever returned `404` from the intake, because the
  app fell back to the `Eu1` intake where the UK1 application ID does not exist.
- **Datadog crash reporting went with it** (`NativeCrashReportEnabled = false`) — crash
  reporting is a RUM feature. Play Console → **Quality → Android vitals → Crashes and ANRs**
  is the source for mobile crashes and ANRs.
- Any fix that reaches the native `useCustomEndpoint` directly is Android-only (the native
  bindings ship as `Datadog.Android.*` packages; there is no iOS equivalent here), which the
  project's Android/iOS parity rule rules out.

Notes: the app's Android minimum is API 31 today (raised for the splash-screen API);
Datadog and Firebase themselves only require API 23. `Site` defaults to
`Eu1` when omitted; consent is currently `Granted` at first launch — add a settings toggle
before any store review that requires opt-in analytics consent. The app sets
`FirstPartyHosts` for the API host with Datadog + W3C `traceparent` tracing headers, so
mobile spans join the API's OTel traces.

## 6. Release version on telemetry

Every log line and every trace carries the release that produced it, so a spike can be
pinned to a deploy without cross-referencing CI:

- **Logs** — the `Version` property, alongside the existing `Application`, `MachineName`
  and `EnvironmentName` enrichers. This is what Better Stack's log sink and the console
  read; Better Stack isn't OTLP-based, so this flat property is its only version signal.
- **Traces/metrics, and for Datadog also logs** — the OTel resource attribute
  `service.version` (Datadog reads it as its `version` tag) plus
  `deployment.environment`/`deployment.environment.name` (→ `env`). Datadog's log
  shipping goes through the same OTLP pipeline as its traces/metrics specifically so all
  three carry this identically — that shared Resource, not the `Version` log property
  above, is what makes unified service tagging (and log/trace correlation) work for
  Datadog. No remapper needed.

Nothing to provision: the value is the deploy's semver tag. `compute-version` in
`deploy-apps-dev.yml` derives it (`v1.2.3`), and that string tags the image as-is. The
`docker build --build-arg VERSION=` that stamps the assembly gets it **without the leading
`v`** (`${TAG#v}`) — MSBuild rejects a v-prefixed `Version`. So an image tagged `v1.2.3`
reports `1.2.3` in logs and traces: the same release, one character apart. Watch for that
when correlating a Datadog `version:` value against a Cloud Run image tag by eye.
`DeploymentInfo` reads it back at startup. Prod redeploys an existing tag, so its
containers report whatever they were built as — which is the point.

Two consequences worth knowing:

- Anything not built by the release pipeline reports **`0.0.0-local`** (the host projects'
  default `<Version>`). Seeing that in Cloud Run means an image was built by hand.
- `DEPLOY_VERSION` (plaintext env var, no section) overrides the baked-in value. It exists
  for one-offs — a hotfix image built out of band, or correcting a mis-stamped revision
  without a rebuild. Normal deploys leave it unset.

Verify after a deploy:

```bash
gcloud run services describe carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --format='value(spec.template.spec.containers[0].image)'   # ...:v1.4.2
# Then in Cloud Run logs, the boot line names it — the same version, minus the "v":
#   "APM configured: engine Datadog shipping ... as CardiTrack.API 1.4.2 (...)"
```

The mobile app does the same in its local log files (`v<version>` on each line), sourced
from `ApplicationDisplayVersion`, which the signed CI builds set from the release tag.

## 7. Later / non-blocking

- Per-app keys/sources (separate tokens for API and Web) — split into per-app `apm-data`
  secrets if quota attribution ever matters; today all services share the one secret.
- Tune volume per service in the environment's tfvars: `traces_sample_ratio` (→
  `Apm__TracesSampleRatio`) and `log_minimum_level` (→ `Serilog__MinimumLevel__Default`);
  flip `apm_metrics_enabled` to turn metrics on or off. Raising `log_minimum_level`
  raises what ships to Datadog too — the sink inherits the Serilog root level — so
  treat it as an ingest-spend change; set `Apm__MinimumLogLevel` by hand only to hold
  the sink **stricter** than the root.
- One-shot job hosts must flush before exit or every span is dropped silently:
  `ApmExtensions.ForceFlushTraces` is called by `CardiTrack.PipelineJobs` before
  `FlushLogsAsync` — any future one-shot host needs the same.
- Monitors and log pipelines live in `infrastructure/datadog/` — see its README for the
  applied monitor IDs (uk1: 33845 worker-host-faulted, 33846 worker-job-failing,
  34150 webhook-notifications-unparseable).
- Switching backends: implement `IApmProvider`, register it in `ApmProviderRegistry`,
  flip `apm_engine` in the environment's tfvars (`infrastructure/environments/<env>.tfvars`),
  and put the new backend's JSON in the same `apm-data` secret — extra fields beyond
  IngestUrl/IngestToken are surfaced to the provider via `Data.Extra`.
