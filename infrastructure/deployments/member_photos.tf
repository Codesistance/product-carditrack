# CardiMember profile photos
#
# A private bucket for full-face member photos — Tier 1 data, Safe Harbor category 17
# (docs/technical/data_protection_architecture.md §5). The apps hold opaque object names
# (members/{cardiMemberId}/{guid}.jpg) and serve reads only as ≤15-minute V4 signed URLs; the
# bucket itself is never publicly reachable. Who touches it and how:
#
#   - The API (its own runtime identity, service_accounts.tf) uploads on photo save and
#     best-effort-deletes on replace/remove/soft delete.
#   - The Worker's OrphanedPhotoCleanupWorker is the enforcement backstop behind those
#     best-effort deletes: it lists the bucket daily and reaps objects no active member
#     references (24-hour crash-window grace).
#
# Access is a dedicated google_storage_bucket_iam_member per workload, never a widened shared
# grant — the same rule the DP key-ring bucket records in cloud_storage.tf.

# Variables
variable "member_photos_bucket_name" {
  description = "Name of the member profile photos GCS bucket"
  type        = string
}

# Resources
resource "google_storage_bucket" "member_photos" {
  name          = var.member_photos_bucket_name
  location      = var.storage_location
  storage_class = var.storage_class
  force_destroy = var.storage_force_destroy

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  # No versioning, and no soft-delete grace, on purpose — the opposite of the main bucket. The
  # retention matrix promises a *hard* delete of the blob on photo removal and member erasure; a
  # noncurrent generation or a soft-deleted object would silently outlive that promise.
  soft_delete_policy {
    retention_duration_seconds = 0
  }

  labels     = var.storage_labels
  depends_on = [google_project_service.storage]
}

# The API's write path: upload on save, delete on replace/remove/soft delete. objectAdmin rather
# than objectCreator+objectViewer because replace must delete the old object, and GCS has no
# narrower role that covers create+get+delete on objects.
resource "google_storage_bucket_iam_member" "api_member_photos" {
  bucket = google_storage_bucket.member_photos.name
  role   = "roles/storage.objectAdmin"
  member = local.api_sa
}

# Signed GET URLs are minted via the IAM Credentials signBlob API from the API's own identity
# (see GcsProfilePhotoStorage), which requires the account to hold tokenCreator on itself.
# Self-referential on purpose: it lets the API sign as itself and nothing else, unlike a
# project-level tokenCreator grant, which would let it mint tokens for any account.
resource "google_service_account_iam_member" "api_member_photos_url_signer" {
  service_account_id = google_service_account.api.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = local.api_sa
}

# The Worker runs as the default compute service account (like every other Cloud Run resource
# here except the webhook receiver, web, the API and the pipeline jobs — a recorded, deliberate
# state; see service_accounts.tf), so the cleanup worker's grant goes to it. Its own binding
# rather than a widening of the API's, per the house rule above. objectAdmin because the sweep
# both lists and deletes.
resource "google_storage_bucket_iam_member" "worker_member_photos" {
  bucket = google_storage_bucket.member_photos.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}
