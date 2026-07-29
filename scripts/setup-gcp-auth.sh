#!/usr/bin/env bash
# Setup GCP Workload Identity Federation for GitHub Actions
# Run once as a project owner: bash scripts/setup-gcp-auth.sh
set -euo pipefail

# ── Variables ─────────────────────────────────────────────────────────────────
PROJECT_ID=carditrack-490120
PROJECT_NUMBER=$(gcloud projects describe $PROJECT_ID --format='value(projectNumber)')
REPO=Codesistance/product-carditrack
SA_NAME=carditrack-deploy
POOL_NAME=carditrack-pool
PROVIDER_NAME=github

# ── Enable required APIs ──────────────────────────────────────────────────────
gcloud services enable \
  cloudresourcemanager.googleapis.com \
  iam.googleapis.com \
  iamcredentials.googleapis.com \
  sts.googleapis.com \
  artifactregistry.googleapis.com \
  run.googleapis.com \
  secretmanager.googleapis.com \
  sqladmin.googleapis.com \
  storage.googleapis.com \
  compute.googleapis.com \
  pubsub.googleapis.com \
  servicenetworking.googleapis.com \
  --project=$PROJECT_ID

# ── Service account ───────────────────────────────────────────────────────────
SA_EMAIL=$SA_NAME@$PROJECT_ID.iam.gserviceaccount.com

if gcloud iam service-accounts describe $SA_EMAIL --project=$PROJECT_ID > /dev/null 2>&1; then
  echo "Service account $SA_EMAIL already exists — skipping"
else
  gcloud iam service-accounts create $SA_NAME \
    --display-name="CardiTrack Deploy" \
    --project=$PROJECT_ID
fi

# ── Grant roles ───────────────────────────────────────────────────────────────
for ROLE in \
  roles/run.admin \
  roles/artifactregistry.admin \
  roles/storage.admin \
  roles/secretmanager.admin \
  roles/cloudsql.admin \
  roles/iam.serviceAccountUser \
  roles/iam.serviceAccountTokenCreator \
  roles/compute.loadBalancerAdmin \
  roles/compute.securityAdmin \
  roles/compute.networkAdmin \
  roles/serviceusage.serviceUsageAdmin \
  roles/resourcemanager.projectIamAdmin; do
  gcloud projects add-iam-policy-binding $PROJECT_ID \
    --member="serviceAccount:$SA_EMAIL" \
    --role="$ROLE" \
    --condition=None
done

# ── Workload Identity Pool ─────────────────────────────────────────────────────
if gcloud iam workload-identity-pools describe $POOL_NAME \
    --location=global --project=$PROJECT_ID > /dev/null 2>&1; then
  echo "Workload Identity Pool $POOL_NAME already exists — skipping"
else
  gcloud iam workload-identity-pools create $POOL_NAME \
    --location=global \
    --display-name="CardiTrack GitHub Pool" \
    --project=$PROJECT_ID
fi

# ── GitHub OIDC Provider ───────────────────────────────────────────────────────
if gcloud iam workload-identity-pools providers describe $PROVIDER_NAME \
    --location=global --workload-identity-pool=$POOL_NAME \
    --project=$PROJECT_ID > /dev/null 2>&1; then
  echo "OIDC provider $PROVIDER_NAME already exists — skipping"
else
  gcloud iam workload-identity-pools providers create-oidc $PROVIDER_NAME \
    --location=global \
    --workload-identity-pool=$POOL_NAME \
    --issuer-uri="https://token.actions.githubusercontent.com" \
    --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository" \
    --attribute-condition="assertion.repository=='${REPO}'" \
    --project=$PROJECT_ID
fi

# ── Bind pool to service account ──────────────────────────────────────────────
gcloud iam service-accounts add-iam-policy-binding $SA_EMAIL \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/attribute.repository/${REPO}" \
  --project=$PROJECT_ID

# ── Print values for _env.yml ──────────────────────────────────────────────────
echo ""
echo "Update _env.yml with:"
echo "  GCP_PROJECT_ID     = $PROJECT_ID"
echo "  GCP_PROJECT_NUMBER = $PROJECT_NUMBER"
echo "  gcp_wif_provider   = projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/providers/${PROVIDER_NAME}"
echo "  gcp_service_account= $SA_EMAIL"
