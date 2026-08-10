---
name: carditrack-trace-triage
description: Triage a CardiTrack trace, error or slow request in Datadog — what CardiTrack's spans and logs actually look like, what a missing trace means here, and how to get from a span to the code. Use alongside the datadog-pup skill when given a trace ID, a traceparent, an ErrorResponse.TraceId from the API, a Datadog APM URL, or asked to investigate a 500 or a slow call in dev or prod.
---

# CardiTrack trace triage

Retrieval is somebody else's job. The `datadog-pup` skill — user-level, not in this repo —
queries the Datadog REST API directly (curl + `DD_API_KEY`/`DD_APP_KEY`, no CLI) against the
CardiTrack org. This skill adds no retrieval of its own; it is what CardiTrack's telemetry
*means* once you have it. If `datadog-pup` isn't available, pull the trace any way you like
(the Datadog UI is fine) — everything below still applies.

## Before anything: the ID you were handed is probably not a trace ID

The API returns `Activity.Current?.Id` in `ErrorResponse.TraceId`
([ExceptionHandlingMiddleware.cs:69](../../../src/Presentation/CardiTrack.API/Middleware/ExceptionHandlingMiddleware.cs#L69)),
which is a **full W3C traceparent** — `00-<32 hex trace>-<16 hex span>-01`, not a bare
trace ID. Take the middle segment as the trace ID; the third segment is the span the
error surfaced on, which is worth expanding first.

The Datadog UI puts a decimal ID in the URL while OTLP-ingested traces are indexed by
128-bit hex. If a lookup comes back empty, try the other form before concluding anything.

## The service names

Every host names itself the same way for **both** logs and traces — bare, lowercase,
from [ApmServiceNames.cs](../../../src/Infrastructure/CardiTrack.Observability/ApmServiceNames.cs):

| `service` | Project |
|---|---|
| `api` | [src/Presentation/CardiTrack.API/](../../../src/Presentation/CardiTrack.API/) |
| `web` | [src/Presentation/CardiTrack.Web/](../../../src/Presentation/CardiTrack.Web/) |
| `worker` | [src/Worker/CardiTrack.Worker/](../../../src/Worker/CardiTrack.Worker/) |

This alignment is deliberate and load-bearing: Datadog joins a log to its trace on
`service`, so the log sink's service field and the OTel resource's `service.name` are fed
the same constant (`AddApmShipping` / `AddApmTracing` both take it). `service:api` in Logs
and `service:api` in APM are the same thing.

> Anything you read — an older doc, a stale skill, a cached answer — that says all three
> apps log as `service:carditrack` is describing the world before that was fixed. Don't
> query it.

Spans and logs also carry `version:<semver>` (the deploy tag, not the assembly version)
and `deployment.environment` / `deployment.environment.name`. Use the environment
attribute to tell dev from prod — **not** the project, since both live in
`carditrack-490120`.

## Triage procedure

**1. Find the error spans.** `RecordException` is on
([ApmExtensions.cs:144](../../../src/Infrastructure/CardiTrack.Observability/ApmExtensions.cs#L144)),
so exceptions arrive as span events with `error.type` / `error.message` / `error.stack`.
The **deepest** errored span is the origin; its ancestors are usually just propagation.

**2. Read the shape of the tree.** Three shapes recur:

- One span dominating duration → that operation is the problem.
- Many sibling `Npgsql` spans → N+1 query. DB spans come from Npgsql's own ActivitySource
  ([ApmExtensions.cs:154](../../../src/Infrastructure/CardiTrack.Observability/ApmExtensions.cs#L154)),
  one per command, parented under the request.
- A long `HttpClient` span → an outbound dependency (Auth0, device provider) is slow or
  failing, not CardiTrack.

**3. Map the span to code**, via the table above. Then read the actual code path before
forming a hypothesis. A span name is evidence, not a diagnosis.

**4. Pull the logs** — but see "Trace↔log correlation" below. Query
`service:<api|web|worker>` over the trace's own time window; you cannot query by trace ID.

**5. Check the client side** if the trace starts at a mobile request. The MAUI app marks
the API host first-party and sends W3C `traceparent`
([MobileApm.cs](../../../src/Presentation/CardiTrack.Mobile/Services/MobileApm.cs)), so
RUM resource events link to these traces. A large gap between RUM resource duration and
server span duration is network or client-side, not server.

## What a missing trace means here

Work down this list before blaming the code — but note the first item, because it is the
opposite of what most Datadog setups assume:

- **Sampling is not your explanation.** `traces_sample_ratio` is `1.0` for api, web and
  worker in **both** [dev.tfvars](../../../infrastructure/environments/dev.tfvars) and
  [prod.tfvars](../../../infrastructure/environments/prod.tfvars), and all three
  `appsettings.json` files pin `1.0` too. (The `ApmOptions` class default is `0.2`, but no
  deployed environment uses it.) Every non-probe request should be traced. A missing trace
  is therefore a real signal, not noise.
- **Health probes are never traced.** `/health` (api, web) and `/healthz` (worker) are
  filtered out at
  [ApmExtensions.cs:145-148](../../../src/Infrastructure/CardiTrack.Observability/ApmExtensions.cs#L145-L148).
- **Traces may not ship at all.** The Datadog OTLP trace intake is org-entitlement-gated.
  If `TraceEndpoint` is absent from `Apm:Data`, the host ships **logs only** and every
  trace lookup returns nothing. It says so at boot — look in Cloud Run logs for
  `APM (Datadog): traces will not ship: TraceEndpoint is not set`
  ([DatadogApmProvider.cs:97-100](../../../src/Infrastructure/CardiTrack.Observability/Providers/DatadogApmProvider.cs#L97-L100)).
  403s from the intake mean the org needs access via Datadog support. Check this before a
  long hunt.
- **Nothing ships when the config is half-set.** `Apm:Engine` plus `IngestUrl` plus
  `IngestToken` are all required, and Terraform's `REPLACE_ME` placeholder counts as
  unset. The boot log names the missing piece.
- **Retention.** Indexed spans go back ~15 days; unsampled ingested spans ~15 minutes.

## Trace↔log correlation

**Logs carry no trace ID.** Serilog is enriched with `FromLogContext`, `WithMachineName`,
`WithEnvironmentName`, `Application` and `Version` only — there is no span enricher, so
nothing writes `dd.trace_id` into log events. Correlating a trace to its logs *by ID* is
impossible today.

*What to do instead:* query `service:<name>` narrowed to the trace's own time window, and
match on timestamp proximity to the errored span.

*Real fix, if this keeps costing time:* add `Serilog.Enrichers.Span`, or set `dd.trace_id`
from `Activity.Current` in a custom enricher, in `CardiTrack.Observability`. That is an
app-code change and needs its own PR — do not fold it into a triage.

**Log level is the other reason a line is missing.** The APM sink inherits the Serilog
root level. In **prod all three services run at `Warning`**, so no Information-level
breadcrumb reaches Datadog at all. In dev, api and worker run at `Information` and web at
`Warning`. Check the level before concluding the code did not run.

## Reporting

Lead with the fault and the evidence for it, then the fix. Include the trace's Datadog URL
so the reader can open it. State explicitly when you are inferring rather than observing —
"no trace found in window" is a fact, "the request was never made" is a guess.

**Health data.** Spans and logs can carry patient health data in tags, URLs and error
messages. Triage locally with the full payload, but never paste raw span or log JSON into
a PR, an issue, or any external tool — quote the specific field you need and redact the
value.
