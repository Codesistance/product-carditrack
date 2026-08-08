# Production Environment Configuration
# terraform apply -var-file="environments/prod.tfvars"

environment  = "prod"
region       = "europe-west2"
project_id   = "carditrack-490120"
project_name = "carditrack"

# Database Credentials
# Password is read from GCP Secret Manager: carditrack-prod-db-password
db_admin_username = "carditrackadmin"

# Container images are deliberately absent: deploy-apps-prod.yml deploys them by tag
# and Terraform ignores the image on every Cloud Run resource. See variables.tf.

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
apm_engine        = "Datadog"
apm_mobile_engine = "Datadog"

# OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql) — bill as custom metrics;
# off until dev proves the volume is affordable, flip to true to enable
apm_metrics_enabled = false

# Serilog root level per service — Warning keeps Cloud Logging and APM ingest lean.
# The APM sink inherits this level, so a raise here ships more to Datadog as well as
# widening Cloud Logging. Turn a single service up (Information/Debug) for an
# investigation, then put it back — prod volume makes that a real spend change.
log_minimum_level = {
  api    = "Warning"
  web    = "Warning"
  worker = "Warning"
}

# Trace head-sampling per service, 0.0-1.0. Full sampling: the ingest cost lever to
# reach for first if Datadog spend needs cutting.
traces_sample_ratio = {
  api    = 1.0
  web    = 1.0
  worker = 1.0
}

# Device pull cadence, per device type. Index-aligned with the DeviceProviders array — element 0
# is Fitbit (Google Health API). Identical to dev on purpose: prod must not be the environment
# where a cadence change is tried first, so dev leads and prod follows once a setting has soaked.
#
# min_pull_interval_minutes is the figure to revisit once the Google Health API request quota is
# known — it bounds how early fresh data can arrive, and calibration may not go below it.
device_pull_params = [
  {
    provider                  = "Fitbit"
    sync_lookback_days        = 3
    audit_lookback_days       = 14 # Widest range the Google Health API accepts for HR/AZM/calorie roll-ups
    min_pull_interval_minutes = 30
    max_pull_interval_minutes = 1440
    max_requests_per_second   = 0 # Unset — no app-side governor until the quota is measured
    dormancy_threshold_pulls  = 0 # 0 disables backoff
    dormancy_backoff_factor   = 2.0
  }
]

# Memorystore for Redis — NOT yet provisioned in prod.
# Known consequence: with no instance the API gets no ConnectionStrings__Redis env var,
# falls back to the appsettings.json localhost:6379 default and times out on every
# distributed-cache write — device linking returns 500 and reports are never cached.
# Flip to true (with STANDARD_HA, which gives failover) to fix it.
enable_redis         = false
redis_tier           = "STANDARD_HA"
redis_memory_size_gb = 1

# Pub/Sub — enabled in prod for real-time messaging
enable_pubsub = true

# Platform audit logging (Cloud SQL audit flags + log sink), 90-day retention
enable_platform_audit_logging = true
audit_retention_days          = 90

# Labels
additional_labels = {
  cost_center = "engineering"
  owner       = "production_team"
  compliance  = "hipaa"
}
