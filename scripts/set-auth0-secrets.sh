#!/usr/bin/env bash
# Populate the Auth0 Secret Manager secrets for one environment.
# Terraform creates these as REPLACE_ME placeholders; until real values are set,
# BOTH the deployed API (JWT validation) and mobile builds (CI stamping) are broken.
#
# Usage: bash scripts/set-auth0-secrets.sh <dev|prod>
# Prompts for each value; press Enter to keep the current secret version.
# Requires: gcloud authenticated as a principal with secretmanager.versions.add.
set -euo pipefail

ENV=${1:?"usage: set-auth0-secrets.sh <dev|prod>"}
case "$ENV" in dev|prod) ;; *) echo "environment must be dev or prod" >&2; exit 1;; esac

PROJECT_ID=carditrack-490120
REGION=europe-west2
PREFIX=carditrack-$ENV

# secret-suffix → human prompt
declare -A SECRETS=(
  [auth0-domain]="Auth0 tenant domain (e.g. your-tenant.eu.auth0.com)"
  [auth0-audience]="API audience identifier (Auth0 API identifier, e.g. https://api.carditrack.com)"
  [auth0-client-id]="API/Web application client id"
  [auth0-client-secret]="API/Web application client secret"
  [auth0-mobile-client-id]="Mobile NATIVE application client id (public client, Password + Refresh Token grants)"
)

# Stable ordering for the prompts
ORDER=(auth0-domain auth0-audience auth0-client-id auth0-client-secret auth0-mobile-client-id)

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
echo "so the API/Worker need a new revision to pick up changes. Either re-run the"
echo "deploy workflow, or force a rollout now with:"
echo
echo "  gcloud run services update $PREFIX-api --region=$REGION --project=$PROJECT_ID \\"
echo "    --update-labels=auth0-config-rollout=\$(date +%s)"
echo
echo "Sanity-check the tenant afterwards (must return access_token AND refresh_token):"
echo
echo "  curl -s -X POST https://<domain>/oauth/token \\"
echo "    -d 'grant_type=http://auth0.com/oauth/grant-type/password-realm' \\"
echo "    -d 'realm=Username-Password-Authentication' \\"
echo "    -d 'client_id=<mobile-client-id>' -d 'audience=<audience>' \\"
echo "    -d 'scope=openid profile email offline_access' \\"
echo "    -d 'username=<test-user>' -d 'password=<test-password>'"
