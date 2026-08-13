# Datadog monitors and log pipelines

Monitor and log-pipeline definitions for CardiTrack, kept in version control so alerting and log
processing are reviewable and reproducible rather than living only as clicked-together state in the
Datadog UI.

## Why JSON and not Terraform

The rest of `infrastructure/` is Terraform, and these monitors are deliberately *not* — yet.
Adding the `DataDog/datadog` provider means giving Terraform a `DD_API_KEY`/`DD_APP_KEY` pair,
deciding where those live relative to the existing GCP secret handling, and taking the monitors
into Terraform state. That is worth doing, but it is its own change with its own review.

These files are the interim: the spec is tracked here, applied via the REST API, and the
resulting monitor IDs are recorded below. Migrating them to `datadog_monitor` resources later is
mechanical — the JSON maps field-for-field.

## Applied monitors

| Spec | Monitor ID | Site | Purpose |
|-|-|-|-|
| `monitors/worker-host-faulted.json` | 33845 | uk1 | Worker process died — an exception escaped a `BackgroundService`, and `StopHost` took the whole host with it |
| `monitors/worker-job-failing.json` | 33846 | uk1 | A single job is throwing on its scheduled tick; the host survives but that job is doing nothing |

Both were created on 2026-08-12 after an incident in which the Worker crash-looped for roughly
six hours across two separate root causes with no alert firing, because the org had no
application-level monitors at all — only Datadog's stock host pack.

The two are complementary, and the split matters. `worker-host-faulted` catches the loud failure
(process death). `worker-job-failing` catches the quiet one, which only became possible once
`CronBackgroundService` started catching exceptions from scheduled ticks: the host now survives a
throwing job, so without this second monitor a job could fail on every tick indefinitely and
nobody would know.

## Log pipelines

| Spec | Pipeline ID | Site | Purpose |
|-|-|-|-|
| `pipelines/otel-severity-to-status.json` | _not yet applied_ | uk1 | Give every log a canonical Datadog status, so severity is queryable |

### Why this exists

Logs reach this org over OTLP, and Datadog copies OTLP `severity_text` into the reserved `status`
field **verbatim**. Serilog writes level names in its own casing, so every log arrives with a status
Datadog does not recognise:

```
status               = "Error"        <- reserved status, taken verbatim
otel.severity_text   = "Error"
otel.severity_number = 17             <- correct OTel ERROR
```

Datadog's canonical statuses are lowercase, so `Error` is not `error`. The observable damage:

- `status:error` matches **nothing** — silently. It returns an empty result set that reads exactly
  like "the service is healthy". Confirmed 2026-08-13 with all status values selected: the raw
  `Information`/`Error`/`Warning` entries held 555/19/6 while Datadog's own `Error`/`Warn`/`Info`
  sat at 0.
- Severity colouring and severity filtering in Log Explorer do not work.
- Error Tracking cannot group anything, because it keys on a recognised error status.
- Both monitors above had to be written against **message text** rather than severity. That works,
  but it means every new monitor has to know this, and one written the obvious way
  (`status:error`) would never fire — the exact failure mode that let the Worker crash-loop for six
  hours unnoticed on 2026-08-12.

### Why the fix is here and not in the app

The application is already sending correct data: `severity_number` is a valid OTel severity (17 for
ERROR). `Serilog.Sinks.OpenTelemetry` exposes no hook for the severity text it writes —
`OpenTelemetrySinkOptions` offers only `FormatProvider`, `RestrictedToMinimumLevel`, `LevelSwitch`
and `OnBeginSuppressInstrumentation` — so this cannot be corrected at the sink without replacing it.
Normalising on ingest also fixes every service at once and needs no redeploy.

The pipeline maps `severity_number` **and** `severity_text` into a canonical value, then adopts it
as the status. Either half is sufficient on its own; both are matched so a log missing one still
maps. Remapping `severity_text` alone would not have been enough — Datadog's status remapper
recognises `Error` and `Warning` but not `Information`, `Fatal` or `Verbose`, which would have left
the largest group (Information) still broken.

| Serilog level | OTel severity_number | Datadog status |
|-|-|-|
| Verbose | 1–4 | trace |
| Debug | 5–8 | debug |
| Information | 9–12 | info |
| Warning | 13–16 | warn |
| Error | 17–20 | error |
| Fatal | 21–24 | critical |

### Applying it

```bash
BASE="https://api.${DD_SITE:-datadoghq.com}"
curl -sS -X POST "$BASE/api/v1/logs/config/pipelines" \
  -H "DD-API-KEY: $DD_API_KEY" -H "DD-APPLICATION-KEY: $DD_APP_KEY" \
  -H "Content-Type: application/json" \
  -d @infrastructure/datadog/pipelines/otel-severity-to-status.json
```

Record the returned `id` in the table above. Updating later is
`PUT /api/v1/logs/config/pipelines/<id>` with the same body.

Two things to check after applying, neither of which the POST itself verifies:

- **It only affects logs ingested from then on.** Existing logs keep their current status, so
  confirm against fresh traffic, not history.
- **Pipeline order matters.** Verify in Logs → Pipelines that nothing ahead of this one also writes
  `status`, and that this pipeline's filter (`source:otlp_log_ingestion`) is actually matching —
  a pipeline that matches nothing looks identical to one that is working.

## Outstanding

- **No notification handle is attached to either monitor.** They evaluate and show state in
  Datadog, but they will not page, email or post to Slack until a recipient is added to the
  `message` field. The org's existing monitors are unhelpful as a reference here — they are stock
  templates and one still contains the literal placeholder `@your-team-handle`.
- Both are scoped `env:dev`, which is the only environment currently shipping telemetry to this
  org. Prod needs its own copies once it ships logs.

## Applying a change

Edit the JSON, then `PUT` it. Creating is `POST /api/v1/monitor`; updating is
`PUT /api/v1/monitor/<id>` with the same body. Keep the table above in sync with any new monitor.

```bash
BASE="https://api.${DD_SITE:-datadoghq.com}"
curl -sS -X PUT "$BASE/api/v1/monitor/33845" \
  -H "DD-API-KEY: $DD_API_KEY" -H "DD-APPLICATION-KEY: $DD_APP_KEY" \
  -H "Content-Type: application/json" \
  -d @infrastructure/datadog/monitors/worker-host-faulted.json
```

Never commit the key values themselves — both are expected in the environment.
