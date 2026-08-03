#!/usr/bin/env bash
# Populate the APM Secret Manager secrets for one environment.
# Terraform creates these as REPLACE_ME placeholders; the app treats placeholders as
# "not configured" and ships nothing, so until real values are set the API and Web
# run fine but no logs/traces reach the APM backend.
#
# Engine selection lives in appsettings (Apm:Engine, "BetterStack" today) — these
# secrets only carry the connection data. For Better Stack, both values come from
# https://telemetry.betterstack.com -> Sources -> your source:
#   ingest URL   = the source's ingesting host (e.g. s123456.eu-nbg-2.betterstackdata.com)
#   ingest token = the source token
#
# Usage: bash scripts/set-apm-secrets.sh <dev|prod>
# Prompts for each value; press Enter to keep the current secret version.
# Requires: gcloud authenticated as a principal with secretmanager.versions.add.
set -euo pipefail

ENV=${1:?"usage: set-apm-secrets.sh <dev|prod>"}
case "$ENV" in dev|prod) ;; *) echo "environment must be dev or prod" >&2; exit 1;; esac

PROJECT_ID=carditrack-490120
REGION=europe-west2
PREFIX=carditrack-$ENV

# secret-suffix → human prompt
declare -A SECRETS=(
  [apm-ingest-url]="APM ingest URL (Better Stack: ingesting host, with or without https://)"
  [apm-ingest-token]="APM ingest token (Better Stack: source token)"
)

# Stable ordering for the prompts
ORDER=(apm-ingest-url apm-ingest-token)

for KEY in "${ORDER[@]}"; do
  SECRET_ID=$PREFIX-$KEY
  CURRENT=$(gcloud secrets versions access latest --secret="$SECRET_ID" --project="$PROJECT_ID" 2>/dev/null || echo "<missing>")
  if [ "$CURRENT" = "REPLACE_ME" ] || [ "$CURRENT" = "<missing>" ]; then
    STATE="(placeholder — needs a real value)"
  else
    STATE="(already set)"
  fi

  echo
  echo "── $SECRET_ID $STATE"
  read -r -p "${SECRETS[$KEY]} [Enter = keep current]: " VALUE
  if [ -z "$VALUE" ]; then
    echo "   kept current version"
    continue
  fi
  printf '%s' "$VALUE" | gcloud secrets versions add "$SECRET_ID" --project="$PROJECT_ID" --data-file=-
  echo "   new version added"
done

echo
echo "Secrets updated. Cloud Run resolves secret-backed env vars at instance start,"
echo "so the API and Web need a new revision to pick up changes. Either re-run the"
echo "deploy workflow, or force a rollout now with:"
echo
echo "  gcloud run services update $PREFIX-api --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=apm-config-rollout=\$(date +%s)"
echo "  gcloud run services update $PREFIX-web --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=apm-config-rollout=\$(date +%s)"
echo
echo "Verify via traces (only Warning+ logs ship, so a healthy quiet app sends no log"
echo "entries — that is expected). Traces are head-sampled at ~20%, so make a burst of"
echo "requests and expect a few of them in the APM backend (Better Stack -> Traces):"
echo
echo "  for i in \$(seq 20); do curl -s -o /dev/null https://api.${ENV}.carditrack.com/api/does-not-exist; done"
