# CardiMember profile photos
#
# Profile photos are full-face images of monitored persons — a Tier 1 direct identifier
# under the data-protection ADR (HIPAA Safe Harbor category 17,
# docs/technical/data_protection_architecture.md §4.2). That classification drives every
# choice below that differs from the main bucket:
#
#   - versioning off and soft-delete retention zero, deliberately inverting the main
#     bucket's defaults: ADR finding #11 records that versioning defeats deletion claims,
#     and erasing a member's photo must be immediate and unrecoverable, not a demotion to
#     a noncurrent version.
#   - the bucket stays fully private (public_access_prevention = "enforced"); clients see
#     photos only through short-lived V4 signed GET URLs the API issues after its own
#     authorization check. No CDN, no public URL, no bucket CORS — browsers never touch
#     the bucket directly.

variable "member_photos_bucket_name" {
  description = "Name of the GCS bucket holding CardiMember profile photos"
  type        = string
}

resource "google_storage_bucket" "member_photos" {
  name          = var.member_photos_bucket_name
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

  labels     = var.storage_labels
  depends_on = [google_project_service.storage]
}

# The API is the only identity on this bucket: it writes re-encoded uploads, deletes
# replaced or removed photos, and signs read URLs. Same rule as the DP key ring in
# cloud_storage.tf — if another workload ever needs photos (e.g. a Worker cleanup job),
# give it its own binding rather than widening this one.
resource "google_storage_bucket_iam_member" "api_member_photos" {
  bucket = google_storage_bucket.member_photos.name
  role   = "roles/storage.objectAdmin"
  member = local.api_sa
}

# V4 signed URLs without an exported key: on Cloud Run the API has no private key
# material, so UrlSigner signs via the IAM Credentials signBlob API — which requires the
# account to hold tokenCreator on itself.
resource "google_service_account_iam_member" "api_self_token_creator" {
  service_account_id = google_service_account.api.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = local.api_sa
}

# The Worker runs as the default compute service account (a recorded, deliberate state — see
# the header comment in service_accounts.tf), so OrphanedPhotoCleanupWorker's grant goes to
# that identity. Its own binding rather than a widening of the API's, per the house rule
# above. objectAdmin because the sweep both lists and deletes.
resource "google_storage_bucket_iam_member" "worker_member_photos" {
  bucket = google_storage_bucket.member_photos.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

output "member_photos_bucket_name" {
  description = "Name of the CardiMember profile photos bucket"
  value       = google_storage_bucket.member_photos.name
}
