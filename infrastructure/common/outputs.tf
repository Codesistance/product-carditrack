output "artifact_registry_repository" {
  description = "Full path of the Artifact Registry repository"
  value       = google_artifact_registry_repository.common.name
}

output "builds_bucket_name" {
  description = "Name of the shared mobile builds GCS bucket"
  value       = google_storage_bucket.common_builds.name
}

output "store_distribution_secret_ids" {
  description = "Secret Manager IDs for mobile store distribution secrets (Apple / Google Play)"
  value       = [for s in google_secret_manager_secret.store_distribution : s.secret_id]
}

# The service's own address, read from the resource rather than constructed.
#
# It was constructed, on the plan's assumption that a Cloud Run URL is
# https://<service>-<project number>.<region>.run.app — the newer of the two formats Google
# issues. This project uses the older one. The first apply produced:
#
#     https://carditrack-common-medgemma-zhsd62wx5a-ew.a.run.app
#
# — a per-project hash and an abbreviated region, matching every other service here
# (carditrack-dev-medgemma-zhsd62wx5a-nw...). The constructed string resolved to nothing, and
# would have been seeded into an environment's MedGemma URL secret looking entirely plausible.
# That is the same failure the medgemma seed post-mortem records, arrived at from the other
# direction.
#
# So the URL cannot be derived by a stack that cannot read this state. M4 takes it from here —
# via CI, or a tfvar carrying the value observed after an apply — and never rebuilds it from
# parts.
output "medgemma_service_url" {
  description = "URL of the shared MedGemma GPU service, or empty when it is not deployed"
  value       = var.medgemma_image != "" ? google_cloud_run_v2_service.medgemma[0].uri : ""
}

output "medgemma_registry" {
  description = "Artifact Registry base path in the GPU service's own region"
  value       = "${var.medgemma_location}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.common_euw1.repository_id}"
}
