# APM Setup Runbook (Operator)

Connects the deployed API, Web, and Worker to the APM backend (**Datadog** — selected per
environment by the `apm_engine` tfvar). The apps are already wired. Two env vars per
service carry the **connection**; the volume knobs (`Apm__MetricsEnabled`,
`Apm__TracesSampleRatio`, `Serilog__MinimumLevel__Default`) are separate and documented
below:

- `Apm__Engine` — plaintext, set by Terraform (`"Datadog"`; `"BetterStack"` also supported)
- `Apm__Data` — Secret Manager-backed (secret `carditrack-<env>-apm-data`) holding one JSON
  object with the selected engine's connection details (per-engine shapes below)

Until the secret holds real JSON the apps run normally and ship nothing — the `REPLACE_ME`
placeholder counts as "not configured". Malformed JSON in the secret fails startup loudly.

Quota guardrails are enforced engine-independently: only Warning+ logs ship,
`/health(z)` is never traced, and metrics (runtime, ASP.NET Core, HttpClient, Npgsql,
GenAI) ship only when the `apm_metrics_enabled` tfvar is true (→ `Apm__MetricsEnabled` env var)
— they bill as custom metrics, so the switch is off by default. Current per-environment
values: **dev `apm_metrics_enabled = true`, prod `= false`**.

All three services now carry the **same** volume settings in `appsettings.json` —
`Serilog:MinimumLevel:Default` and `Apm:MinimumLogLevel` both `Warning`,
`Apm:TracesSampleRatio` `1.0` (full sampling, DB commands included via Npgsql spans).
Terraform then sets two of those per service, so one service can be tuned without
touching the others (both dev and prod tfvars currently list all three services at the
baseline):

| tfvar (object, one attribute per service) | env var | baseline |
| --- | --- | --- |
| `log_minimum_level` | `Serilog__MinimumLevel__Default` | `Warning` |
| `traces_sample_ratio` | `Apm__TracesSampleRatio` | `1.0` |

Every attribute is optional, so `log_minimum_level = { api = "Debug" }` turns the API up
and leaves Web and Worker at `Warning`. Two things to know when you do that:

- The APM sink keeps filtering at `Apm:MinimumLogLevel` (`Warning`), so turning a
  service up floods Cloud Logging only — it does not increase what ships to Datadog.
  Lower the shipped floor too by setting the `Apm__MinimumLogLevel` env var by hand.
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

### How each app identifies itself

Each host reports as its **app type**, lowercase — `api`, `web`, `worker` (the mobile app
is separate: `carditrack-mobile`, set in `MobileApm`). That is what the **Service** facet
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
   sends return 403 "organization is not allowed", request access via **Help → Support**
   (or the CSM). The full URL becomes the `TraceEndpoint` field. Skipping it is fine —
   the apps ship logs only until it's set (and log a startup Warning saying so).
4. Metrics need no extra field: when `apm_metrics_enabled = true`, the intake URL is
   derived from the site (`https://otlp.<site>/v1/metrics`); an optional `MetricsEndpoint`
   field overrides it. The same org entitlement applies.

Datadog `Apm__Data` shape:

```json
{"IngestUrl":"uk1.datadoghq.com","IngestToken":"<api key>","TraceEndpoint":"https://otlp.uk1.datadoghq.com/v1/traces"}
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
# Filter by app with service:api / service:web / service:worker, and confirm the
# release landed with the Version facet (or "version:<semver>" in the query bar).
# env:dev / env:prod separates the environments — mobile already reports the same two
# values. Logs with no env at all mean neither ASPNETCORE_ENVIRONMENT nor
# DEPLOY_ENVIRONMENT reached the revision; the startup Warning names both.
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
printf '%s' '{"ClientToken":"<pub...>","ApplicationId":"<uuid>","Site":"Uk1","CustomEndpoint":"https://browser-intake-uk1-datadoghq.com"}' \
  | gcloud secrets versions add carditrack-dev-apm-mobile-data --project=carditrack-490120 --data-file=-
```

**`CustomEndpoint` is mandatory on UK1.** `Datadog.Maui`'s `DatadogSite` enum names only
`Us1`, `Us3`, `Us5`, `Eu1`, `Ap1`, `Ap2` and `Us1Fed` — there is no `Uk1` member, even
though the native Android/iOS SDKs it wraps support the site. `CustomEndpoint` points the
Logs and RUM features at the UK1 intake host directly
(`https://browser-intake-uk1-datadoghq.com`, the same base URL dd-sdk-android's own `UK1`
entry uses). Only one version of `Datadog.Maui` has ever shipped (0.2.0), so this is not
something a package bump fixes. Note the property is undocumented for RUM — Datadog
documents `CustomEndpoint` for Logs and Traces only — so treat the first build as the
verification that it works.

3. The next mobile CI build stamps engine + data in (`-p:ApmEngine=... -p:ApmData=<base64>`);
   placeholder data stamps as empty, which disables monitoring entirely — unprovisioned
   environments and local builds ship nothing. A bad engine name or malformed JSON logs
   and skips at app startup (monitoring must never brick the app).
4. Verify: install the internal-track build, open the app, then Datadog →
   **RUM → Sessions** (and force a crash in a test build for Error Tracking).

Notes: the Datadog SDK raised the Android minimum from API 21 to 23; `Site` defaults to
`Eu1` when omitted, and a `Site` the enum does not name **disables monitoring** unless a
`CustomEndpoint` is set alongside it (better nothing than telemetry delivered to the wrong
region — the app logs the reason at startup to the on-device Serilog file, so it is
readable from a Release build too); consent is currently `Granted` at first launch — add a settings
toggle before any store review that requires opt-in analytics consent. RUM sessions are
**unsampled** (`SessionSampleRate = 100`) — fine at beta scale, revisit before broad
rollout. The app also sets `FirstPartyHosts` for the API host with Datadog + W3C
`traceparent` tracing headers, so RUM resource timings correlate with the API's OTel
traces (RUM→APM correlation).

## 6. Release version on telemetry

Every log line and every trace carries the release that produced it, so a spike can be
pinned to a deploy without cross-referencing CI:

- **Logs** — the `Version` property, alongside the existing `Application`, `MachineName`
  and `EnvironmentName` enrichers.
- **Traces/metrics** — the OTel resource attribute `service.version`, which Datadog reads
  as its `version` tag (the second half of unified service tagging; `env` is not wired
  yet). Note that the Serilog side ships `Version` as a plain log attribute — pair it with
  a Datadog remapper if you want log-side unified tagging too.

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
  flip `apm_metrics_enabled` to turn metrics on or off. Lowering the *shipped* log floor
  below `Warning` still needs an `Apm__MinimumLogLevel` env var set by hand — deliberate,
  so a debugging session cannot quietly raise ingest.
- Switching backends: implement `IApmProvider`, register it in `ApmProviderRegistry`,
  flip `apm_engine` in the environment's tfvars (`infrastructure/environments/<env>.tfvars`),
  and put the new backend's JSON in the same `apm-data` secret — extra fields beyond
  IngestUrl/IngestToken are surfaced to the provider via `Data.Extra`.
