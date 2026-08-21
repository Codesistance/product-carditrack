# Common Infrastructure Configuration
# Shared resources with no environment distinction.
# terraform apply -var-file="environments/common.tfvars"

project_id   = "carditrack-490120"
region       = "europe-west2"
project_name = "carditrack"

# ── MedGemma (shared GPU service) ────────────────────────────────────────────────────────────
# europe-west1: Cloud Run offers no L4 in europe-west2 where the rest of this estate sits, and
# west1 is the nearest EU region that does (DPIA v0.11 R-A4/M4 accepts west1/west4 alongside
# west2, so this is a region change, not a residency one).
medgemma_location = "europe-west1"

# Cloud Run's hello image, so the service and its IAM exist to be deployed into before CI has
# ever pushed a model image here. Replaced by the real image on the first deploy — Terraform
# ignores changes to it thereafter, so this line stays as the seed it is.
medgemma_image = "us-docker.pkg.dev/cloudrun/container/hello"

# Dev's callers only. Constructed emails rather than a lookup — this root cannot read the
# environment stacks' state. Prod's two go in once its service accounts are confirmed to exist,
# so this list never claims a grant that means nothing.
medgemma_invoker_members = [
  "serviceAccount:carditrack-dev-api@carditrack-490120.iam.gserviceaccount.com",
  "serviceAccount:carditrack-dev-pipeline@carditrack-490120.iam.gserviceaccount.com",
]
