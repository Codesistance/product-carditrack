# APM Setup Runbook (Operator)

Connects the deployed API and Web to the APM backend (Better Stack today). The apps are
already wired: `Apm:Engine` is committed in appsettings, and the ingest URL/token arrive as
Secret Manager-backed env vars (`Apm__Data__IngestUrl`, `Apm__Data__IngestToken`). Until the
secrets hold real values the apps run normally and ship nothing — `REPLACE_ME` placeholders
count as "not configured".

Free-tier guardrails are enforced in code (`CardiTrack.Observability`): only Warning+ logs
ship, traces are head-sampled at 20%, `/health` is never traced, and metrics are not exported.

## 1. Create the Better Stack source

1. Sign in at https://telemetry.betterstack.com with the cloud-ops account
   (cloudoperations@codesistance.com — not a personal account).
2. Sources → **Connect source**, platform **OpenTelemetry**, name it `carditrack-<env>`
   (one shared source per environment; the `Application` log property and OTel
   `service.name` distinguish API from Web within it).
3. From the source's settings, note:
   - **Source token**
   - **Ingesting host** (e.g. `s123456.eu-nbg-2.betterstackdata.com`)

## 2. Provision the secrets

Terraform has already created the placeholder secrets (`carditrack-<env>-apm-ingest-url`,
`carditrack-<env>-apm-ingest-token`) with compute-SA read access; if this environment predates
them, run `terraform apply` first.

```bash
bash scripts/set-apm-secrets.sh dev   # prompts for the two values
```

## 3. Roll out

Cloud Run resolves secret-backed env vars at instance start, so force new revisions:

```bash
gcloud run services update carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
gcloud run services update carditrack-dev-web --region=europe-west2 --project=carditrack-490120 \
  --update-labels=apm-config-rollout=$(date +%s)
```

## 4. Verify (before blaming app code)

```bash
# Traces: sampled at ~20%, so send a burst; expect a handful in Better Stack -> Traces
for i in $(seq 20); do curl -s -o /dev/null https://api.dev.carditrack.com/api/does-not-exist; done

# Logs: a quiet healthy app ships NOTHING (only Warning+) — absence of logs is not a fault.
# Check the env vars actually reached the revision:
gcloud run services describe carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --format=json | grep -A3 Apm__   # both vars should reference the apm-* secrets
# Startup crash-looping after rollout => bad Apm:Engine value (unknown engines fail fast);
# check Cloud Run logs for "Unknown APM engine".
```

## 5. Later / non-blocking

- Per-app sources (separate tokens for API and Web) — paste different values into per-app
  secrets if quota attribution ever matters; today both apps share the one source.
- Raise `Apm:TracesSampleRatio` / lower `Apm:MinimumLogLevel` via plaintext env vars
  (`Apm__TracesSampleRatio`, `Apm__MinimumLogLevel`) if the plan is upgraded.
- Switching backends: implement `IApmProvider`, register it in `ApmProviderRegistry`,
  set `Apm__Engine`, and put the new backend's values in these same two secrets.
