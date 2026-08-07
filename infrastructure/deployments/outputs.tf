# Deployments Module Outputs
# Exposes outputs from all GCP deployment resources

# Data
data "google_project" "current" {}

# Outputs
output "api_service_name" {
  description = "Name of the API Cloud Run service"
  value       = google_cloud_run_v2_service.api.name
}

output "api_service_url" {
  description = "URL of the API Cloud Run service"
  value       = google_cloud_run_v2_service.api.uri
}

output "web_service_name" {
  description = "Name of the Web Cloud Run service"
  value       = google_cloud_run_v2_service.web.name
}

output "web_service_url" {
  description = "URL of the Web Cloud Run service"
  value       = google_cloud_run_v2_service.web.uri
}

output "web_service_hostname" {
  description = "Hostname of the Web Cloud Run service"
  value       = replace(google_cloud_run_v2_service.web.uri, "https://", "")
}

output "cloud_sql_instance_name" {
  description = "Name of the Cloud SQL instance"
  value       = google_sql_database_instance.main.name
}

output "cloud_sql_connection_name" {
  description = "Cloud SQL instance connection name (for Cloud SQL Auth Proxy)"
  value       = google_sql_database_instance.main.connection_name
}

output "cloud_sql_public_ip" {
  description = "Public IP of the Cloud SQL instance"
  value       = google_sql_database_instance.main.public_ip_address
}

output "cloud_sql_database_name" {
  description = "Name of the Cloud SQL database"
  value       = google_sql_database.main.name
}

output "storage_bucket_name" {
  description = "Name of the GCS bucket"
  value       = google_storage_bucket.main.name
}

output "storage_bucket_url" {
  description = "URL of the GCS bucket"
  value       = google_storage_bucket.main.url
}

output "secret_manager_project" {
  description = "Secret Manager project path"
  value       = "projects/${data.google_project.current.project_id}"
}

output "db_password_secret_name" {
  description = "Secret Manager secret name for database password"
  value       = google_secret_manager_secret.db_password.name
}

output "redis_host" {
  description = "Private IP of the Memorystore instance (if enabled)"
  value       = length(google_redis_instance.main) > 0 ? google_redis_instance.main[0].host : null
}

output "redis_port" {
  description = "Memorystore port — 6378 while in-transit encryption is on (if enabled)"
  value       = length(google_redis_instance.main) > 0 ? google_redis_instance.main[0].port : null
}

output "pubsub_topic_name" {
  description = "Cloud Pub/Sub topic name (if enabled)"
  value       = length(google_pubsub_topic.realtime) > 0 ? google_pubsub_topic.realtime[0].name : null
}

output "pubsub_topic_id" {
  description = "Cloud Pub/Sub topic ID (if enabled)"
  value       = length(google_pubsub_topic.realtime) > 0 ? google_pubsub_topic.realtime[0].id : null
}
