#!/usr/bin/env bash
# Populate the APM Secret Manager secret for one environment.
# Terraform creates carditrack-<env>-apm-data as a REPLACE_ME placeholder; the app treats
# placeholders as "not configured" and ships nothing, so until a real value is set the
# API, Web, and Worker run fine but no logs/traces reach the APM backend.
#
# The deployment contract is two env vars:
#   Apm__Engine  — plaintext, set by Terraform via the apm_engine tfvar ("Datadog" today)
#   Apm__Data    — this secret: one JSON object with the engine's connection details
#
# Datadog (see docs/technical/apm_setup_runbook.md section 1):
#   IngestUrl     = the Datadog site (e.g. datadoghq.eu, us5.datadoghq.com)
#   IngestToken   = the API key (Organization Settings -> API Keys)
#   TraceEndpoint = optional org-specific agentless OTLP traces intake URL; skip => logs only
# Better Stack (https://telemetry.betterstack.com -> Sources):
#   IngestUrl   = the source's ingesting host (e.g. s123456.eu-nbg-2.betterstackdata.com)
#   IngestToken = the source token (no third value)
#
# Usage: bash scripts/set-apm-secrets.sh <dev|prod>
# Prompts for the values and composes the JSON; press Enter on both to keep the current
# secret version.
# Requires: gcloud authenticated as a principal with secretmanager.versions.add.
set -euo pipefail

ENV=${1:?"usage: set-apm-secrets.sh <dev|prod>"}
case "$ENV" in dev|prod) ;; *) echo "environment must be dev or prod" >&2; exit 1;; esac

PROJECT_ID=carditrack-490120
REGION=europe-west2
PREFIX=carditrack-$ENV
SECRET_ID=$PREFIX-apm-data

CURRENT=$(gcloud secrets versions access latest --secret="$SECRET_ID" --project="$PROJECT_ID" 2>/dev/null || echo "<missing>")
if [ "$CURRENT" = "REPLACE_ME" ] || [ "$CURRENT" = "<missing>" ]; then
  STATE="(placeholder — needs a real value)"
else
  STATE="(already set)"
fi

echo "── $SECRET_ID $STATE"
if [ "$STATE" = "(already set)" ]; then
  echo "   current: $CURRENT"
fi
echo
read -r -p "APM ingest URL (Datadog: site, e.g. datadoghq.eu) [Enter = keep current]: " URL
read -r -p "APM ingest token (Datadog: API key) [Enter = keep current]: " TOKEN

if [ -z "$URL" ] && [ -z "$TOKEN" ]; then
  echo "   kept current version"
else
  if [ -z "$URL" ] || [ -z "$TOKEN" ]; then
    echo "Both values are needed to compose the JSON — provide URL and token together." >&2
    exit 1
  fi
  read -r -p "Trace endpoint (Datadog agentless OTLP URL; Enter = logs only): " TRACE_ENDPOINT
  # Compose via python-free JSON escaping: values are hosts/tokens/URLs, so forbid the
  # two characters that would break the JSON rather than escape them.
  case "$URL$TOKEN$TRACE_ENDPOINT" in *'"'*|*'\'*) echo 'Values must not contain `"` or `\`.' >&2; exit 1;; esac
  if [ -n "$TRACE_ENDPOINT" ]; then
    JSON=$(printf '{"IngestUrl":"%s","IngestToken":"%s","TraceEndpoint":"%s"}' "$URL" "$TOKEN" "$TRACE_ENDPOINT")
  else
    JSON=$(printf '{"IngestUrl":"%s","IngestToken":"%s"}' "$URL" "$TOKEN")
  fi
  printf '%s' "$JSON" | gcloud secrets versions add "$SECRET_ID" --project="$PROJECT_ID" --data-file=-
  echo "   new version added"
fi

echo
echo "Cloud Run resolves secret-backed env vars at instance start, so the API, Web, and"
echo "Worker need a new revision to pick up changes. Either re-run the deploy workflow,"
echo "or force a rollout now with:"
echo
echo "  gcloud run services update $PREFIX-api --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=apm-config-rollout=\$(date +%s)"
echo "  gcloud run services update $PREFIX-web --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=apm-config-rollout=\$(date +%s)"
echo "  gcloud run services update $PREFIX-worker --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=apm-config-rollout=\$(date +%s)"
echo
echo "Verify via traces (only Warning+ logs ship, so a healthy quiet app sends no log"
echo "entries — that is expected). Traces are head-sampled at ~20%, so make a burst of"
echo "requests and expect a few in the APM backend (Datadog -> APM -> Traces; needs the"
echo "TraceEndpoint value — logs-only setups check Datadog -> Logs -> Live Tail instead):"
echo
echo "  for i in \$(seq 20); do curl -s -o /dev/null https://api.${ENV}.carditrack.com/api/does-not-exist; done"
