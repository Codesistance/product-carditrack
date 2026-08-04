# Production Environment Configuration
# terraform apply -var-file="environments/prod.tfvars"

environment  = "prod"
region       = "europe-west2"
project_id   = "carditrack-490120"
project_name = "carditrack"

# Database Credentials
# Password is read from GCP Secret Manager: carditrack-prod-db-password
db_admin_username = "carditrackadmin"

# Container Images — updated by deploy-apps-prod.yml workflow
# Format: REGION-docker.pkg.dev/PROJECT_ID/REPO/IMAGE:TAG
api_container_image = "europe-west2-docker.pkg.dev/carditrack-490120/carditrack/api:latest"
web_container_image = "europe-west2-docker.pkg.dev/carditrack-490120/carditrack/web:latest"

# Custom Domains (optional — leave empty to use Cloud Run default URLs)
api_custom_domain = ""
web_custom_domain = ""

# Cloud Run
cloud_run_cpu    = "2"
cloud_run_memory = "1Gi"

# Cloud SQL — Regional HA, larger disk, deletion protection on
cloud_sql_tier                = "db-custom-2-7680"
cloud_sql_disk_size_gb        = 100
cloud_sql_ha_enabled          = true
cloud_sql_deletion_protection = true
cloud_sql_public_ip_enabled   = false # Private only; Cloud Run connects via Auth Proxy socket

# Storage
storage_location = "EU"
storage_class    = "STANDARD"

# APM (logs + traces shipping) — engine name from ApmProviderRegistry; empty disables
apm_engine = "Datadog"

# Pub/Sub — enabled in prod for real-time messaging
enable_pubsub = true

# HIPAA Compliance — full audit logging, 90-day retention
enable_hipaa_compliance = true
audit_retention_days    = 90

# Labels
additional_labels = {
  cost_center = "engineering"
  owner       = "production_team"
  compliance  = "hipaa"
}
