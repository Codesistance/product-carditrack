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
    provider     = "GoogleHealth"
    device_types = ["Fitbit", "GooglePixelWatch"] # Hardware brands this API serves — the DeviceType→HealthApi mapping

    # Google OAuth + Health API endpoints and read scopes (non-secret; ClientId/Secret stay in
    # Secret Manager as devices-fitbit-client-*).
    authorization_url    = "https://accounts.google.com/o/oauth2/v2/auth"
    token_url            = "https://oauth2.googleapis.com/token"
    api_base_url         = "https://health.googleapis.com"
    token_lifetime_hours = 1
    scopes = [
      "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly",
      "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly",
      "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
      # Paired-device telemetry (battery level/status). Optional: a connection granted before this
      # scope shipped keeps syncing and simply reports no battery until the wearer reconnects.
      # Added before the consent screen was submitted for verification — it must appear in the
      # restricted-scope justification alongside the three read bundles.
      "https://www.googleapis.com/auth/googlehealth.settings.readonly",
    ]
    additional_authorization_params    = { access_type = "offline" } # Without it Google issues no refresh token
    first_consent_authorization_params = { prompt = "consent" }      # First grant only — re-consent is how a refresh token is re-issued

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

# Memorystore for Redis — NOT yet provisioned in prod.
# Known consequence: with no instance the API gets no ConnectionStrings__Redis env var,
# falls back to the appsettings.json localhost:6379 default and times out on every
# distributed-cache write — device linking returns 500 and reports are never cached.
# Flip to true (with STANDARD_HA, which gives failover) to fix it.
enable_redis         = false
redis_tier           = "STANDARD_HA"
redis_memory_size_gb = 1

# Dev test-push endpoint — never in prod. Stated rather than left to the default so the answer
# is visible in the file people read to find out what prod runs; the root variable's validation
# rejects `true` here regardless, and the app refuses to route the controller in prod anyway.
enable_dev_push_token = false

# Pub/Sub — enabled in prod for real-time messaging
enable_pubsub = true

# Platform audit logging (Cloud SQL audit flags + log sink), 90-day retention
enable_platform_audit_logging = true
audit_retention_days          = 90

# Cloud Run OOM alerting (issue #171). Slack stays off until the one-time manual Console
# OAuth step is done — see infrastructure/deployments/alerting.tf.
enable_oom_alerting       = true
alert_notification_emails = ["cloudoperations@codesistance.com"]
enable_slack_alerts       = false
alert_slack_channel_id    = ""

# Labels — "audit = platform" mirrors enable_platform_audit_logging; the label deliberately
# does not claim a certified HIPAA posture (see the variable's rename rationale in variables.tf).
additional_labels = {
  cost_center = "engineering"
  owner       = "production_team"
  audit       = "platform"
}

# AI pipeline job (digest generation): off — prod has no MedGemma service yet (medgemma_image
# unset). Enable together with the MedGemma deploy.
enable_pipeline_jobs = false

# Webhook receiver: off - enable together with the AI pipeline rollout (the topic exists, but
# nothing consumes it in prod and the endpoint is untested against live Google delivery).
enable_webhook_receiver = false

# Public slot on Vertex — same D6 flip dev made 2026-08-21 (IAM auth via the api SA's
# unconditional aiplatform.user grant, EU regional endpoint; the gemini-api-key secret stays
# mounted only for pre-Vertex images). Not optional and not "dev leads" material any more:
# the defaults this file used to fall back to were Kind=Gemini + gemini-2.0-flash, and Google
# retired gemini-2.0-flash on 2026-06-01 (Gemini API; 2026-03-03 on Vertex), so the fallback
# config has been a dead model since then. gemini-2.5-flash on europe-west2 (the
# public_ai_location default) is the pairing dev proved between its 2026-08-21 flip and
# 2026-08-25, when dev moved on to soak gemini-3.5-flash — so prod pins the soaked config, not
# dev's current one. gemini-2.5-flash is itself retired ~2026-10-16 with the rest of the 2.5
# family; prod follows dev to gemini-3.5-flash once that soak is done — see
# docs/technical/vertex_ai_setup.md §3. Same rollback coupling
# as dev: a pre-#416 image cannot parse Kind=VertexGemini, so rolling back past that flip is
# image + tfvars together.
public_ai_kind  = "VertexGemini"
public_ai_model = "gemini-2.5-flash"

# The same shared GPU service dev uses — one instance serves every environment. Prod has no
# MedGemma consumers wired yet (enable_pipeline_jobs is off and medgemma_image is empty), so this
# seeds a secret nothing currently reads; it is set because the variable is required and a
# placeholder here would be the very thing the seed's own note warns about.
medgemma_service_url = "https://carditrack-common-medgemma-zhsd62wx5a-ew.a.run.app"
