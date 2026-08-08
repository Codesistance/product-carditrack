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
apm_engine        = "Datadog"
apm_mobile_engine = "Datadog"

# OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql) — bill as custom metrics
apm_metrics_enabled = true

# Serilog root level per service — Warning keeps Cloud Logging and APM ingest lean.
# The APM sink inherits this level, so a raise here ships more to Datadog as well as
# widening Cloud Logging. API and Worker run at Information in dev deliberately: this is
# where wearable syncs and OAuth bounces are diagnosed, and their per-run detail
# (WearableSync summaries, per-connection outcomes) is Information. Web stays lean.
log_minimum_level = {
  api    = "Information"
  web    = "Warning"
  worker = "Information"
}

# Trace head-sampling per service, 0.0-1.0. Full sampling: the ingest cost lever to
# reach for first if Datadog spend needs cutting.
traces_sample_ratio = {
  api    = 1.0
  web    = 1.0
  worker = 1.0
}

# Memorystore for Redis — standalone instance is enough for dev
enable_redis         = true
redis_tier           = "BASIC"
redis_memory_size_gb = 1

# Pub/Sub
enable_pubsub = false # Disabled in dev

# Platform audit logging (Cloud SQL audit flags + log sink)
enable_platform_audit_logging = false
audit_retention_days          = 30

# Labels
additional_labels = {
  cost_center = "development"
  owner       = "dev_team"
}
