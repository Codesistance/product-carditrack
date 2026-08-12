# Datadog monitors

Monitor definitions for CardiTrack, kept in version control so alerting is reviewable and
reproducible rather than living only as clicked-together state in the Datadog UI.

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
