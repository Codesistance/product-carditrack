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
  androidpublisher.googleapis.com \
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
  roles/redis.admin \
  roles/iam.serviceAccountUser \
  roles/iam.serviceAccountTokenCreator \
  roles/logging.configWriter \
  roles/monitoring.alertPolicyEditor \
  roles/monitoring.notificationChannelEditor \
  roles/firebase.admin \
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
# job_workflow_ref lets a service account be bound to one workflow file rather
# than to the whole repository, which is what scopes the digest identity below.
ATTR_MAPPING="google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.job_workflow_ref=assertion.job_workflow_ref"

if gcloud iam workload-identity-pools providers describe $PROVIDER_NAME \
    --location=global --workload-identity-pool=$POOL_NAME \
    --project=$PROJECT_ID > /dev/null 2>&1; then
  echo "OIDC provider $PROVIDER_NAME already exists — skipping"
else
  gcloud iam workload-identity-pools providers create-oidc $PROVIDER_NAME \
    --location=global \
    --workload-identity-pool=$POOL_NAME \
    --issuer-uri="https://token.actions.githubusercontent.com" \
    --attribute-mapping="$ATTR_MAPPING" \
    --attribute-condition="assertion.repository=='${REPO}'" \
    --project=$PROJECT_ID
fi

# Applied on every run, not only at creation: the mapping gained
# attribute.job_workflow_ref after this provider already existed, and the branch
# above only skips. Adding a mapping is additive — principalSets bound on
# attribute.repository keep working — so this is safe to reapply.
gcloud iam workload-identity-pools providers update-oidc $PROVIDER_NAME \
  --location=global \
  --workload-identity-pool=$POOL_NAME \
  --attribute-mapping="$ATTR_MAPPING" \
  --project=$PROJECT_ID

# ── Digest posting identity ───────────────────────────────────────────────────
# Deliberately separate from carditrack-deploy, which holds project-level
# roles/secretmanager.admin and so can read every secret in the project. This
# account gets NO project-level roles: its only grant is secretAccessor on
# carditrack-common-slack-bot-token, made per secret in
# infrastructure/common/secret_manager.tf.
DIGEST_SA_NAME=carditrack-digest
DIGEST_SA_EMAIL=$DIGEST_SA_NAME@$PROJECT_ID.iam.gserviceaccount.com

if gcloud iam service-accounts describe $DIGEST_SA_EMAIL --project=$PROJECT_ID > /dev/null 2>&1; then
  echo "Service account $DIGEST_SA_EMAIL already exists — skipping"
else
  gcloud iam service-accounts create $DIGEST_SA_NAME \
    --display-name="CardiTrack Digest Poster" \
    --project=$PROJECT_ID
fi

# The whole claim of this account is that it holds no project-level role, so
# assert it rather than assume it. The branch above only skips creation, so an
# account that picked up a role elsewhere would otherwise sail through and the
# per-secret binding in Terraform would be describing an isolation that is not
# there. Fail closed and make a human look.
DIGEST_PROJECT_ROLES=$(gcloud projects get-iam-policy $PROJECT_ID \
  --flatten="bindings[].members" \
  --filter="bindings.members:serviceAccount:${DIGEST_SA_EMAIL}" \
  --format="value(bindings.role)")

if [ -n "$DIGEST_PROJECT_ROLES" ]; then
  echo "ERROR: $DIGEST_SA_EMAIL holds project-level roles it must not have:" >&2
  echo "$DIGEST_PROJECT_ROLES" | sed 's/^/  /' >&2
  echo "" >&2
  echo "This account exists to read one secret. Any project-level role defeats" >&2
  echo "that and makes the per-secret grant in" >&2
  echo "infrastructure/common/secret_manager.tf meaningless. Remove them with:" >&2
  echo "  gcloud projects remove-iam-policy-binding $PROJECT_ID \\" >&2
  echo "    --member=serviceAccount:$DIGEST_SA_EMAIL --role=<role>" >&2
  exit 1
fi

# ── Bind pool to service account ──────────────────────────────────────────────
gcloud iam service-accounts add-iam-policy-binding $SA_EMAIL \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/attribute.repository/${REPO}" \
  --project=$PROJECT_ID

# Bound to the posting workflow, not to the repository. A repository-wide
# principalSet would let any workflow in the repo — every deploy workflow
# already requests id-token: write — authenticate as this account and read the
# Slack token, which would make "scoped identity" untrue.
DIGEST_WORKFLOW_REF="${REPO}/.github/workflows/post-digest.yml@refs/heads/main"

# Drop the repository-wide binding if an earlier run of this script added one,
# so reruns converge on the narrow grant instead of accumulating both.
gcloud iam service-accounts remove-iam-policy-binding $DIGEST_SA_EMAIL \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/attribute.repository/${REPO}" \
  --project=$PROJECT_ID 2>/dev/null || true

gcloud iam service-accounts add-iam-policy-binding $DIGEST_SA_EMAIL \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/attribute.job_workflow_ref/${DIGEST_WORKFLOW_REF}" \
  --project=$PROJECT_ID

# ── Print values for _env.yml ──────────────────────────────────────────────────
echo ""
echo "Update _env.yml with:"
echo "  GCP_PROJECT_ID     = $PROJECT_ID"
echo "  GCP_PROJECT_NUMBER = $PROJECT_NUMBER"
echo "  gcp_wif_provider   = projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/providers/${PROVIDER_NAME}"
echo "  gcp_service_account= $SA_EMAIL"
echo "  gcp_digest_service_account = $DIGEST_SA_EMAIL"
