# Artifact Registry
# Central Docker image repository shared across all environments.


resource "google_project_service" "common_artifactregistry" {
  service            = "artifactregistry.googleapis.com"
  disable_on_destroy = false
}

resource "google_artifact_registry_repository" "common" {
  location      = var.region
  repository_id = "${var.project_name}-common"
  format        = "DOCKER"
  description   = "Central Docker image registry for CardiTrack services"
  depends_on    = [google_project_service.common_artifactregistry]

  vulnerability_scanning_config {
    enablement_config = "DISABLED"
  }

  cleanup_policy_dry_run = false

  cleanup_policies {
    id     = "keep-last-50"
    action = "KEEP"
    most_recent_versions {
      keep_count = 50
    }
  }

  cleanup_policies {
    id     = "delete-old"
    action = "DELETE"
    condition {
      tag_state = "ANY"
    }
  }
}

resource "google_artifact_registry_repository_iam_member" "common_ci_writer" {
  location   = var.region
  repository = google_artifact_registry_repository.common.name
  role       = "roles/artifactregistry.writer"
  member     = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}

resource "google_artifact_registry_repository_iam_member" "common_cloud_run_reader" {
  location   = var.region
  repository = google_artifact_registry_repository.common.name
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# ── Mirror in the GPU service's own region ───────────────────────────────────────────────────
# Cloud Run pulls an image from the region it runs in. The MedGemma image is multi-GB of model
# blobs, and pulling that cross-region on every cold start — which this service now does by
# design, since it scales to zero — is the difference between a cold start being seconds and
# being minutes. So the image lives in both: europe-west2 for everything else, europe-west1
# beside the accelerator.
#
# Same cleanup policy as the primary. Kept as a separate resource rather than a for_each over
# regions because the two have different reasons to exist and only one of them is conditional on
# the GPU service surviving its first month's billing.
resource "google_artifact_registry_repository" "common_euw1" {
  location      = var.medgemma_location
  repository_id = "${var.project_name}-common-${replace(var.medgemma_location, "-", "")}"
  format        = "DOCKER"
  description   = "CardiTrack images for services running in ${var.medgemma_location} (the MedGemma GPU service)"
  depends_on    = [google_project_service.common_artifactregistry]

  vulnerability_scanning_config {
    enablement_config = "DISABLED"
  }

  cleanup_policy_dry_run = false

  cleanup_policies {
    id     = "keep-last-50"
    action = "KEEP"
    most_recent_versions {
      keep_count = 50
    }
  }

  cleanup_policies {
    id     = "delete-old"
    action = "DELETE"
    condition {
      tag_state = "ANY"
    }
  }
}

resource "google_artifact_registry_repository_iam_member" "common_euw1_ci_writer" {
  location   = var.medgemma_location
  repository = google_artifact_registry_repository.common_euw1.name
  role       = "roles/artifactregistry.writer"
  member     = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}

resource "google_artifact_registry_repository_iam_member" "common_euw1_cloud_run_reader" {
  location   = var.medgemma_location
  repository = google_artifact_registry_repository.common_euw1.name
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}
