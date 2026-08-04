# Development Environment Configuration
# terraform apply -var-file="environments/dev.tfvars"

environment  = "dev"
region       = "europe-west2"
project_id   = "carditrack-490120"
project_name = "carditrack"

# Database Credentials
# Password is read from GCP Secret Manager: carditrack-dev-db-password
db_admin_username = "carditrackadmin"

# Custom Domains (WAF + CDN + GCLB enabled when set)
api_custom_domain = "api.dev.carditrack.com"
web_custom_domain = "app.dev.carditrack.com"

# Cloud Run
cloud_run_cpu    = "1"
cloud_run_memory = "512Mi"

# Cloud SQL
cloud_sql_tier                = "db-f1-micro" # Shared-core for dev
cloud_sql_disk_size_gb        = 10
cloud_sql_ha_enabled          = false
cloud_sql_deletion_protection = false
cloud_sql_public_ip_enabled   = false # DB is private-only; Cloud Run connects via Auth Proxy socket over the VPC private network

# Storage
storage_location = "EU"
storage_class    = "STANDARD"

# APM (logs + traces shipping) — engine name from ApmProviderRegistry; empty disables
apm_engine = "BetterStack"

# Pub/Sub
enable_pubsub = false # Disabled in dev

# HIPAA Compliance
enable_hipaa_compliance = false
audit_retention_days    = 30

# Labels
additional_labels = {
  cost_center = "development"
  owner       = "dev_team"
}
