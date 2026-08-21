# GCP API Enablement
# Enables required Google Cloud APIs for CardiTrack

# Variables
variable "project_id" {
  description = "GCP project ID"
  type        = string
}

variable "region" {
  description = "GCP region"
  type        = string
}

# Resources
resource "google_project_service" "run" {
  service            = "run.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "sql" {
  service            = "sqladmin.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "storage" {
  service            = "storage.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "secretmanager" {
  service            = "secretmanager.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "monitoring" {
  service            = "monitoring.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "logging" {
  service            = "logging.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "pubsub" {
  count              = var.enable_pubsub ? 1 : 0
  service            = "pubsub.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "redis" {
  count              = var.enable_redis ? 1 : 0
  service            = "redis.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "cloudscheduler" {
  count              = var.enable_pipeline_jobs ? 1 : 0
  service            = "cloudscheduler.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "firebase" {
  count              = var.enable_push_notifications ? 1 : 0
  service            = "firebase.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "fcm" {
  count              = var.enable_push_notifications ? 1 : 0
  service            = "fcm.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "compute" {
  service            = "compute.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "servicenetworking" {
  service            = "servicenetworking.googleapis.com"
  disable_on_destroy = false
}

# Read-only, for pricing. The Cloud Billing API carries the SKU catalogue — the exact per-second
# rates for Cloud Run vCPU, memory and GPU in a given region — which is what turns a serving-cost
# comparison from list-price arithmetic into a real one (medgemma_serving_architecture.md, MS-1).
#
# Enabling it grants nothing by itself: reading an actual bill needs roles/billing.viewer on the
# *billing account*, which is a different resource from this project. And the catalogue is not
# spend — there is no REST API for what was actually charged, only the BigQuery export, which is a
# billing-account operation and does not backfill.
resource "google_project_service" "cloudbilling" {
  service            = "cloudbilling.googleapis.com"
  disable_on_destroy = false
}

# Vertex AI — the Public and Rewrite AI slots' VertexGemini kinds call Gemini through the
# project's regional aiplatform endpoint (docs/technical/vertex_ai_setup.md). Enabling the API
# grants nothing by itself: calls are authorised per service account via roles/aiplatform.user
# in service_accounts.tf.
resource "google_project_service" "aiplatform" {
  service            = "aiplatform.googleapis.com"
  disable_on_destroy = false
}
