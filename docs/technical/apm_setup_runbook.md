# APM Setup Runbook (Operator)

Connects the deployed API, Web, and Worker to the APM backend (Better Stack today). The apps
are already wired; the whole deployed contract is two env vars per service:

- `Apm__Engine` — plaintext, set by Terraform (`"BetterStack"`)
- `Apm__Data` — Secret Manager-backed (secret `carditrack-<env>-apm-data`) holding one JSON
  object: `{"IngestUrl":"<ingesting host>","IngestToken":"<source token>"}`

Until the secret holds real JSON the apps run normally and ship nothing — the `REPLACE_ME`
placeholder counts as "not configured". Malformed JSON in the secret fails startup loudly.

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

Terraform has already created the placeholder secret (`carditrack-<env>-apm-data`) with
compute-SA read access; if this environment predates it, run `terraform apply` first.

```bash
bash scripts/set-apm-secrets.sh dev   # prompts for URL + token, composes the JSON
```

(Equivalent by hand:
`printf '{"IngestUrl":"s123456...betterstackdata.com","IngestToken":"..."}' | gcloud secrets versions add carditrack-dev-apm-data --project=carditrack-490120 --data-file=-`)

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
# Traces: sampled at ~20%, so send a burst; expect a handful in Better Stack -> Traces
for i in $(seq 20); do curl -s -o /dev/null https://api.dev.carditrack.com/api/does-not-exist; done

# Logs: a quiet healthy app ships NOTHING (only Warning+) — absence of logs is not a fault.
# Check the env vars actually reached the revision:
gcloud run services describe carditrack-dev-api --region=europe-west2 --project=carditrack-490120 \
  --format=json | grep -A3 Apm__   # Apm__Engine plaintext + Apm__Data referencing the apm-data secret
# Startup crash-looping after rollout => bad Apm__Engine value ("Unknown APM engine" in
# Cloud Run logs) or malformed JSON in the apm-data secret ("not valid JSON").
```

## 5. Later / non-blocking

- Per-app sources (separate tokens for API and Web) — split into per-app `apm-data` secrets
  if quota attribution ever matters; today both apps share the one source.
- Raise `Apm:TracesSampleRatio` / lower `Apm:MinimumLogLevel` via plaintext env vars
  (`Apm__TracesSampleRatio`, `Apm__MinimumLogLevel`) if the plan is upgraded.
- Switching backends: implement `IApmProvider`, register it in `ApmProviderRegistry`,
  flip `apm_engine` in the environment's tfvars (`infrastructure/environments/<env>.tfvars`),
  and put the new backend's JSON in the same `apm-data` secret — extra fields beyond
  IngestUrl/IngestToken are surfaced to the provider via `Data.Extra`.
