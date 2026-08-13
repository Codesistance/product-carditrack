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
| `pipelines/otel-severity-to-status.json` | _fallback — apply only if the app-side enricher fails verification_ | uk1 | Give every log a canonical Datadog status, so severity is queryable |

> **The primary fix is in code, not here.** `DatadogLogStatusEnricher`
> (`src/Infrastructure/CardiTrack.Observability/Providers/`) stamps a canonical lowercase `level`
> attribute on every shipped log, which Datadog's intake reads in preference to `severity_text`
> (the attribute check comes first in DataDog/opentelemetry-mapping-go's
> `pkg/otlp/logs/transform.go`, whose comment says the list mirrors the backend). Code wins over
> console config here: it is versioned, tested, needs no manual step per org, and covers prod
> automatically the day it ships logs. That precedence is exercised but not *documented* for the
> direct intake, so the enricher must be verified once against dev after deploy — if a fresh log
> arrives with `status:error` (lowercase), the enricher works and this pipeline spec stays
> unapplied. If it arrives as `Error`, apply this pipeline as below and record the ID.

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

### This is a regression from the OTLP migration, not a standing gap

None of this needed configuring before 2026-08-11, which is worth knowing before anyone concludes
the pipeline is redundant ceremony. Grouping every log by `status` and `source` splits perfectly
along the ingestion path, with no overlap:

| `source` | statuses seen | |
|-|-|-|
| `csharp` (classic intake, to 2026-08-11) | `info` 5,518 · `warn` 408 · `error` 244 · `emergency` 28 | canonical |
| `otlp_log_ingestion` (OTLP, since) | `Information` 6,656 · `Error` 897 · `Fatal` 871 · `Warning` 205 | verbatim |

The classic intake tagged logs `source:csharp`, which auto-installs Datadog's C# integration
pipeline, whose status remapper reads a `level` attribute — and the classic intake put the Serilog
level exactly there. Datadog normalised it for free, `Fatal` included, which is where the
`Fatal` → `emergency` mapping this pipeline keeps comes from.

PR #190 moved logs onto OTLP so they would finally correlate with traces. That fixed correlation and
silently cost the status mapping: OTLP logs arrive as `source:otlp_log_ingestion`, which matches no
pipeline carrying a status remapper, so `severity_text` lands in `status` untouched.

Measured bluntly: `status:error` over 30 days matches the 244 old `csharp` records and **none** of
the 897 `Error` records from OTLP.

This pipeline is the price of that migration. Reverting to the classic intake would restore the
status mapping and give the log/trace correlation back up; keeping OTLP and adding one remapper
keeps both.

### The app-side route, and why the sink itself can't do it

`Serilog.Sinks.OpenTelemetry` exposes no hook for the severity text it writes —
`OpenTelemetrySinkOptions` offers only `FormatProvider`, `RestrictedToMinimumLevel`, `LevelSwitch`
and `OnBeginSuppressInstrumentation`. But the intake's attribute precedence
(`status`/`severity`/`level`/`syslog.severity` before `severity_text`) means an ordinary Serilog
*enricher* can supply the status as a log-record attribute without touching the sink — which is
what `DatadogLogStatusEnricher` does, scoped inside `DatadogApmProvider.AddLogShipping` so the
`level` attribute never reaches the console or another engine. This pipeline remains the
ingest-side equivalent should that precedence ever prove not to hold on the direct intake.

There is no stock alternative to reach for first. The
[integration pipeline library](https://docs.datadoghq.com/logs/log_configuration/pipelines/#integration-pipeline-library)
auto-installs a pipeline on first receipt of a matching `source`, and `source:otlp_log_ingestion`
has been arriving for months having installed exactly one — "OTEL Serverless Log Enrichment", which
carries no severity handling. If a library pipeline covered this, it would already be here.
"Preprocessing for JSON logs" cannot help either: it runs only on JSON, and these arrive as OTLP
protobuf (`otel_source:protobuf_endpoint`).

### One processor, not six

The whole job is a single
[status remapper](https://docs.datadoghq.com/logs/log_configuration/processors/log_status_remapper/),
because that processor already normalises case-insensitively **by prefix** — `err*` → error,
`w*` → warning, `i*` → info, `d*`/`t*`/`v*` → debug, `f*` → emerg. Serilog's level names all fall
out correctly on their own, with no mapping table to maintain:

| Serilog level | severity_text | Datadog status |
|-|-|-|
| Verbose | `Verbose` | debug |
| Debug | `Debug` | debug |
| Information | `Information` | info |
| Warning | `Warning` | warning |
| Error | `Error` | error |
| Fatal | `Fatal` | emergency |

`Fatal` landing on `emergency` rather than `critical` is the one judgement call, and it is left as
is: Serilog logs `Fatal` when the process is dying, which is what Datadog's top severity is for. A
category processor ahead of the remapper could force `critical` instead, at the cost of a second
processor to keep correct.

**Do not source `otel.severity_number`.** It is the more "correct-looking" field and it is a trap:
the remapper's numeric rule interprets integers as *syslog* severities 0–7, so OTel's 9 (INFO),
13 (WARN) and 17 (ERROR) all fall through its "everything else maps to info" case — every error
would be silently relabelled as info, which is worse than the current state because it looks fixed.

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

### How this sits alongside the org's existing pipelines

The org has two, both `is_read_only: true` (Pipeline Library integrations, which cannot be edited —
only cloned or disabled), verified 2026-08-13:

| # | Name | Filter | Writes `status`? |
|-|-|-|-|
| 1 | `C#` | `source:csharp` | **Yes** — a `status-remapper` on `level`/`Level`/`@l` |
| 2 | `OTEL Serverless Log Enrichment` | `source:otlp_log_ingestion` | No — its two category processors target `origin` |

That table *is* the root cause. Datadog's stock .NET pipeline would have set the status correctly,
but it only matches `source:csharp`; logs arriving over OTLP are tagged `source:otlp_log_ingestion`
and never reach it. The pipeline that does match them writes only `origin`. So nothing sets `status`
for these logs, and the field keeps whatever the OTLP intake copied in verbatim.

This new pipeline shares filter #2's exact query, which is safe: logs are **not** routed to a single
pipeline. Per Datadog's docs, "each log that comes through the pipelines is tested against every
pipeline filter. If it matches a filter, then all the processors are applied sequentially before
moving to the next pipeline." A log therefore passes through the OTEL enrichment pipeline *and* this
one. Placing this one after it is correct and loses no enrichment — and cloning the read-only
pipeline to bolt these processors on would have been the wrong move, duplicating twelve enrichment
processors this change has no business owning.

Position relative to pipeline #2 is in any case not load-bearing: this pipeline reads the raw OTLP
severity field, which that pipeline never touches.

One rule to remember if more pipelines are added later: **only the first status remapper a log meets
applies**, across all matching pipelines. There is no conflict today — #1 is scoped to
`source:csharp` and #2 has no status remapper — but a future pipeline placed above this one with its
own status remapper would silently win.

### After applying

Two things the POST does not verify:

- **It only affects logs ingested from then on.** Existing logs keep their current status, so
  confirm against fresh traffic, not history — and dev can be quiet for hours at a stretch, so
  generate a request rather than waiting.
- **Confirm the filter is actually matching.** In Logs → Pipelines, a pipeline matching nothing
  looks identical to one that is working. The canonical `Error`/`Warn`/`Info` values in the status
  facet should start taking the counts that currently sit on `Error`/`Warning`/`Information`.

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
