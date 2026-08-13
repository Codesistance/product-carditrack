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

# The Data Protection key ring is read by exactly one identity: google_service_account.web, via
# google_storage_bucket_iam_member.web_dpkeys in service_accounts.tf. The default compute SA held
# objectAdmin here until 2026-08-13, back when it was also the runtime identity for web; that grant
# is removed now that web runs as itself and its revision has been confirmed on the new account.
#
# Worth removing rather than leaving: the ring is deliberately unencrypted on GCS (accepted, because
# it protects antiforgery tokens only), so every additional identity that can read it widens an
# exception that was justified on the basis of being narrow.
#
# Do not re-add a grant here for a workload that merely runs alongside web. If something else ever
# needs the ring, give it its own binding and think about whether sharing a key ring is what you
# actually want.
