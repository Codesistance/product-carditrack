# Health data exports (PDF, CSV, FHIR R4)
#
# A rendered export is the most concentrated health data we hold in one object: a named
# CardiMember with their readings, alerts and devices for a whole period, in a file built to
# be handed to a clinician. That drives the choices below, which follow member_photos.tf
# rather than the main bucket:
#
#   - versioning off and soft-delete retention zero. ADR finding #11 records that versioning
#     defeats deletion claims, and the download window ExpiredReportCleanupWorker enforces
#     has to mean the object is gone, not demoted to a noncurrent version.
#   - a lifecycle rule as the backstop under that worker. The retention window is application
#     config (Storage:Reports:Retention, 7 days); this rule deletes anything the worker
#     somehow missed, so an object cannot outlive its window because a job stopped running.
#     Deliberately slack against the app's window — the worker is the mechanism, this is the
#     guarantee.
#   - the bucket stays fully private (public_access_prevention = "enforced") and, unlike
#     member photos, nothing ever signs a URL into it: exports are streamed back through the
#     API so the ownership check and the [AuditHealthDataAccess] row apply to every download.
#     A signed URL here would be a bearer capability to a complete health record.

variable "report_exports_bucket_name" {
  description = "Name of the GCS bucket holding rendered health-data exports"
  type        = string
}

variable "report_exports_lifecycle_days" {
  description = "Backstop age, in days, after which an export object is deleted regardless of worker state"
  type        = number
  default     = 14
}

resource "google_storage_bucket" "report_exports" {
  name          = var.report_exports_bucket_name
  location      = var.storage_location
  storage_class = var.storage_class
  force_destroy = var.storage_force_destroy

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = false
  }

  soft_delete_policy {
    retention_duration_seconds = 0
  }

  lifecycle_rule {
    action {
      type = "Delete"
    }
    condition {
      age = var.report_exports_lifecycle_days
    }
  }

  labels     = var.storage_labels
  depends_on = [google_project_service.storage]
}

# The API writes rendered exports and reads them back to stream to the caregiver. objectAdmin
# rather than objectCreator + objectViewer because a regenerated export overwrites, and a
# failed generation should be able to clean up after itself.
resource "google_storage_bucket_iam_member" "api_report_exports" {
  bucket = google_storage_bucket.report_exports.name
  role   = "roles/storage.objectAdmin"
  member = local.api_sa
}

# The Worker runs as the default compute service account (a recorded, deliberate state — see
# the header comment in service_accounts.tf), so ExpiredReportCleanupWorker's grant goes to
# that identity. Its own binding rather than a widening of the API's, the same house rule
# member_photos.tf follows. objectAdmin because the sweep deletes.
resource "google_storage_bucket_iam_member" "worker_report_exports" {
  bucket = google_storage_bucket.report_exports.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

output "report_exports_bucket_name" {
  description = "Name of the health-data exports bucket"
  value       = google_storage_bucket.report_exports.name
}
