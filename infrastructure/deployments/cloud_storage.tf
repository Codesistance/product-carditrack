# Cloud Storage
# Manages GCS bucket for application storage

# Variables
variable "storage_bucket_name" {
  description = "Name of the GCS bucket"
  type        = string
}

variable "storage_location" {
  description = "GCS bucket location (US, EU, ASIA, or specific region)"
  type        = string
  default     = "EU"
}

variable "storage_class" {
  description = "GCS storage class (STANDARD, NEARLINE, COLDLINE, ARCHIVE)"
  type        = string
  default     = "STANDARD"
}

variable "storage_force_destroy" {
  description = "Allow bucket deletion even if non-empty"
  type        = bool
  default     = false
}

variable "storage_labels" {
  description = "Labels for storage resources"
  type        = map(string)
  default     = {}
}

# Resources
resource "google_storage_bucket" "main" {
  name          = var.storage_bucket_name
  location      = var.storage_location
  storage_class = var.storage_class
  force_destroy = var.storage_force_destroy

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = true
  }

  labels     = var.storage_labels
  depends_on = [google_project_service.storage]
}

# ASP.NET Data Protection key ring for CardiTrack.Web — antiforgery tokens must
# survive container recycling and validate across Cloud Run instances, so the
# key ring persists here instead of the container filesystem.
resource "google_storage_bucket" "dataprotection_keys" {
  name          = "${var.storage_bucket_name}-dp-keys"
  location      = var.storage_location
  storage_class = var.storage_class
  force_destroy = var.storage_force_destroy

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = true
  }

  labels     = var.storage_labels
  depends_on = [google_project_service.storage]
}

# Superseded, kept deliberately for one release. Web now runs as its own identity
# (google_service_account.web) and reads this bucket through google_storage_bucket_iam_member
# .web_dpkeys; nothing else mounts the key ring, so this grant is dead the moment web's revision is
# confirmed on the new account.
#
# Not removed in the same change that moves the identity, and the reason is the 2026-08-13 apply
# failure: if the revision update fails on IAM propagation while this grant has already gone, web
# is left running as the compute SA with no access to its own key ring — antiforgery breaks on a
# public service. Removing it is a second, separately verifiable apply. Do that once web is healthy
# on the new identity; leaving it is a lingering read on an unencrypted key ring, so it is worth
# actually doing rather than forgetting.
resource "google_storage_bucket_iam_member" "web_dataprotection_keys" {
  bucket = google_storage_bucket.dataprotection_keys.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}
