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

# Webhook receiver's own domain, fronted by the same GCLB + Cloud Armor WAF as api/web
# (see "Health webhook receiver" below and load_balancer.tf). DNS is managed on Cloudflare —
# after apply, point an A record at the `lb_ip_address` output (same IP api/web already use).
webhook_custom_domain = "webhook.dev.carditrack.com"

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

# Kept warm, which the default (0) does not do. MedGemma is on a latency-sensitive path —
# the Dashboard's status line is generated inside the request a caregiver is waiting on — and
# scaling to zero makes that path pay a cold start: the image pull plus the ~59s model load.
#
# It buys nothing beyond that, and the second half of this comment used to claim otherwise: that
# a dead instance "takes its prefix cache with it". There is no prefix cache to lose. Gemma 3
# uses sliding-window attention and llama.cpp will not restore a KV checkpoint under SWA, so the
# fixed instruction block is reprocessed from token zero on every call, warm or cold
# (`cached n_tokens = 0` measured on every generation, 2026-08-13; the container now sets
# LLAMA_ARG_CACHE_RAM=0 because the cache could only ever cost). See the rationale on
# medgemma_min_instances in deployments/cloud_run.tf, which has the measurements.
#
# What warmth actually protects, measured over 24h on 2026-08-16/17: 13 API calls a day. The
# other ~276 were background generation (assessor and digest jobs), which waits happily. That
# is the trade to weigh when the digest cadence question is settled — at today's arrival rate
# of roughly one call every five minutes there is no idle window for min=0 to exploit anyway,
# so this stays 1 until a quiet window exists. Revisit it and the scheduler cadences together,
# never one alone.
#
# Set here rather than on the variable's default so prod, which has no MedGemma service yet,
# does not silently inherit a warm 4 vCPU / 16 Gi instance the day it gets one — with
# cpu_idle = false that bills continuously and is the largest single line item on this estate.
medgemma_min_instances = 1

# The Rewrite host stays warm too (issue #397): at the default 0 a member-chat send pays the
# full scale-from-zero — image pull, startup probe, ~59s model load — which outlasts the API's
# whole retry budget (~2 min), so every interactive chat failed unless background traffic
# happened to have warmed the instance (verified live 2026-08-20; traces in the issue). Unlike
# medgemma this service keeps cpu_idle = true, so a quiet warm instance bills at the idle rate —
# a far smaller line than medgemma's always-on 4/16.
# OLLAMA_KEEP_ALIVE follows this setting in the module, pinning the model in memory so the warm
# instance answers without a reload.
rewrite_min_instances = 1

# 16Gi, taking this comment's own earlier escalation rule ("if 8Gi ever OOMs, the next stop is
# 4 vCPU / 16Gi"): 8Gi HAS OOM'd, repeatedly and measurably — killed at 8209–8283 MiB during
# and just after model load on 2026-08-20 (17:47–18:05 and again on revision 00009 at 21:45,
# the alert that reopened this), even with OLLAMA_CONTEXT_LENGTH=8192 verifiably in effect.
# The 7.2 GiB p99 measured on the medgemma service does not transfer: on Cloud Run this
# service's 4B q4 weights count against the limit roughly twice (mmap'd blob page cache plus
# llama.cpp's repacked CPU buffers) before the vision tower, vocab and KV — the full series of
# measurements lives on rewrite_memory's default in deployments/cloud_run.tf. 16Gi matches
# that default; this line stays only to keep dev explicit and to carry this history. A smaller
# rewrite model (bakeoff in progress, 2026-08-20) is the route back down — not a smaller limit
# under the current model.
rewrite_memory = "16Gi"

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

# Cloud NAT — retired. Phase 2 of the MedGemma IAM change (#238), applied here only because
# phase 1 has landed and been verified in dev: every Cloud Run service and job now runs
# PRIVATE_RANGES_ONLY egress, with no ALL_TRAFFIC declaration left anywhere, so nothing routes
# through the gateway and it bills a fixed hourly rate for an idle resource.
#
# Checked before flipping: no Auth0, Datadog or Google Health IP allowlist anywhere in the repo or
# docs pins our egress address — the only static IP is the inbound load balancer's. Direct Cloud
# Run egress uses Google's shared pool, so that check is what makes this safe. NAT egress logs go
# away with the gateway; nothing consumed them.
#
# Prod deliberately keeps this true until its own phase 1 applies and is verified — flipping it
# there now would collapse both phases into one apply, which is the exact ordering hazard the
# two-phase split exists to avoid (Terraform will not order a destroy against unrelated updates).
enable_cloud_nat = false

# Memorystore for Redis — standalone instance is enough for dev
enable_redis         = true
redis_tier           = "BASIC"
redis_memory_size_gb = 1

# Dev test-push endpoint (notification_engine.md §13). Provisions Dev:PushTokenKey and binds
# it to the API, which is the only thing that makes POST /api/v1/dev/push exist. On here
# because reproducing a push or notification-sound problem otherwise means waiting for a real
# alert — PushCanaryWorker, the only other trigger, has never run anywhere.
#
# The endpoint is anonymous by necessity (the point is to send without a signed-in caller), so
# the HMAC key is its whole authorization. Read that as: anyone holding this secret can send a
# Safety push to any user in dev. That is an acceptable trade for dev data and no real
# caregivers; it is why the root variable refuses to pair this with prod at all.
enable_dev_push_token = true

# Pub/Sub — on since the webhook receiver landed: the realtime topic is its publish target.
enable_pubsub = true

# Google Health webhook receiver — reached only via the GCLB/WAF at webhook_custom_domain
# above (secret-authenticated). The Subscriber registration against Google is a separate
# provisioning step once the domain resolves and its managed cert is ACTIVE — see
# docs/llm_design.md "Provisioning the webhook subscriber" and the production setup runbook §7.
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

# Firebase Cloud Messaging — provisioning ahead of the Phase 3 push send path (#108),
# so the Firebase apps exist and a real test push can be sent before closing that issue.
enable_push_notifications = true

# Labels
additional_labels = {
  cost_center = "development"
  owner       = "dev_team"
}
