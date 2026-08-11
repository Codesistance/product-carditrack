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

# Worker specifically needs more headroom than api/web: it ingests granular wearable payloads
# (up to 100k samples per series, 4 series per device) that the other services never touch.
# 2026-08-11 OOM incident: a WearableSync run against a high-cadence wearer hit the 512Mi
# ceiling almost immediately. Matches what prod already runs.
worker_cloud_run_memory = "1Gi"

# MedGemma — dev runs the real model, not a stand-in, so that an assessment made here means
# the same thing it will mean in prod. The value below is only the create seed: it gates
# whether the service exists at all, and deploy-apps-dev.yml re-points the image on every
# MedGemma build (the resource ignores image changes). Terraform has to create the service
# first, because `gcloud run deploy` would otherwise create it at Cloud Run's 1 CPU /
# 512 Mi default with no VPC attachment, and Terraform would then collide with it.
medgemma_image = "us-docker.pkg.dev/cloudrun/container/hello"

# The AI pipeline's scheduled job (digest generation) — on in dev, where MedGemma runs.
# Same seed-image mechanics as the medgemma service above.
enable_pipeline_jobs = true

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

# OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql, GenAI) — bill as custom metrics
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

# Device pull cadence, per device type. Index-aligned with the DeviceProviders array — element 0
# is Fitbit (Google Health API).
#
# Set to today's fixed behaviour: a 30-minute floor and no dormancy backoff. The floor is the
# figure to revisit once the Google Health API request quota is known — it is what bounds how
# early fresh data can arrive, and calibration may not go below it. Backoff turns on by setting
# dormancy_threshold_pulls above zero, once there is enough measured data to justify a value.
device_pull_params = [
  {
    provider                  = "Fitbit"
    sync_lookback_days        = 3
    backfill_days             = 90 # History fetched behind a new connection, a chunk per pull
    backfill_chunk_days       = 7  # ~91 requests per pull on top of the routine window
    audit_lookback_days       = 14 # Widest range the Google Health API accepts for HR/AZM/calorie roll-ups
    min_pull_interval_minutes = 10
    max_pull_interval_minutes = 1440
    max_requests_per_second   = 0 # Unset — no app-side governor until the quota is measured
    dormancy_threshold_pulls  = 0 # 0 disables backoff
    dormancy_backoff_factor   = 2.0
  }
]

# Memorystore for Redis — standalone instance is enough for dev
enable_redis         = true
redis_tier           = "BASIC"
redis_memory_size_gb = 1

# Pub/Sub — on since the webhook receiver landed: the realtime topic is its publish target.
enable_pubsub = true

# Google Health webhook receiver (public ingress, secret-authenticated). The Subscriber
# registration against Google is a separate provisioning step once the service URL exists —
# see docs/llm_design.md "Provisioning the webhook subscriber".
enable_webhook_receiver = true

# Platform audit logging (Cloud SQL audit flags + log sink)
enable_platform_audit_logging = false
audit_retention_days          = 30

# Cloud Run OOM alerting (issue #171: carditrack-dev-worker OOM'd 2026-08-11 and went
# undetected for ~3 hours). Slack stays off until the one-time manual Console OAuth step is
# done — see infrastructure/deployments/alerting.tf.
enable_oom_alerting       = true
alert_notification_emails = ["cloudoperations@codesistance.com"]
enable_slack_alerts       = false
alert_slack_channel_id    = ""

# Labels
additional_labels = {
  cost_center = "development"
  owner       = "dev_team"
}
