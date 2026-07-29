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
