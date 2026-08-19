# Root Outputs for CardiTrack Infrastructure

output "gcp_project_id" {
  description = "GCP project ID"
  value       = var.project_id
}

output "gcp_region" {
  description = "GCP region"
  value       = var.region
}

output "api_service_url" {
  description = "URL of the API Cloud Run service"
  value       = module.deployments.api_service_url
}

output "api_service_name" {
  description = "Name of the API Cloud Run service"
  value       = module.deployments.api_service_name
}

output "web_service_url" {
  description = "URL of the Web Cloud Run service"
  value       = module.deployments.web_service_url
}

output "web_service_name" {
  description = "Name of the Web Cloud Run service"
  value       = module.deployments.web_service_name
}

output "cloud_sql_connection_name" {
  description = "Cloud SQL instance connection name"
  value       = module.deployments.cloud_sql_connection_name
}

output "cloud_sql_instance_name" {
  description = "Name of the Cloud SQL instance"
  value       = module.deployments.cloud_sql_instance_name
}

output "cloud_sql_database_name" {
  description = "Name of the Cloud SQL database"
  value       = module.deployments.cloud_sql_database_name
}

output "storage_bucket_name" {
  description = "Name of the Cloud Storage bucket"
  value       = module.deployments.storage_bucket_name
}

output "storage_bucket_url" {
  description = "URL of the Cloud Storage bucket"
  value       = module.deployments.storage_bucket_url
}

output "member_photos_bucket_name" {
  description = "Name of the CardiMember profile photos bucket"
  value       = module.deployments.member_photos_bucket_name
}

output "secret_manager_project" {
  description = "Secret Manager project path"
  value       = module.deployments.secret_manager_project
}

output "pubsub_topic_name" {
  description = "Cloud Pub/Sub topic name (if enabled)"
  value       = module.deployments.pubsub_topic_name
}

output "pubsub_topic_id" {
  description = "Cloud Pub/Sub topic ID (if enabled)"
  value       = module.deployments.pubsub_topic_id
}

output "lb_ip_address" {
  description = "Global HTTPS LB IP (if any custom domain is configured) — point each configured domain's Cloudflare A record here"
  value       = module.deployments.lb_ip_address
}
