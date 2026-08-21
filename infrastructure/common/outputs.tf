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

# The address the environment stacks seed into their own MedGemma URL secret. Deterministic —
# project number and region, not the resource's uri — precisely so a stack that cannot read this
# state can still name it. See the secret seed in deployments/secret_manager.tf.
output "medgemma_service_url" {
  description = "URL of the shared MedGemma GPU service, or empty when it is not deployed"
  value = var.medgemma_image != "" ? format(
    "https://%s-common-medgemma-%s.%s.run.app",
    var.project_name, data.google_project.current.number, var.medgemma_location
  ) : ""
}

output "medgemma_registry" {
  description = "Artifact Registry base path in the GPU service's own region"
  value       = "${var.medgemma_location}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.common_euw1.repository_id}"
}
