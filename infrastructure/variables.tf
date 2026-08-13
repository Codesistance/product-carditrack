# Root Variables for CardiTrack Infrastructure
# These are provided via environment-specific tfvars files

variable "project_id" {
  description = "GCP project ID"
  type        = string
}

variable "environment" {
  description = "Environment name (dev or prod)"
  type        = string
  validation {
    condition     = contains(["dev", "prod"], var.environment)
    error_message = "Environment must be dev or prod."
  }
}

variable "region" {
  description = "GCP region for resources"
  type        = string
  default     = "europe-west2"
}

variable "project_name" {
  description = "Project name for resource naming"
  type        = string
  default     = "carditrack"
}

# Database Configuration
variable "db_admin_username" {
  description = "Cloud SQL administrator username"
  type        = string
}

# Cloud Run Configuration
variable "cloud_run_cpu" {
  description = "CPU allocation for Cloud Run services"
  type        = string
  default     = "1"
}

variable "cloud_run_memory" {
  description = "Memory allocation for Cloud Run services"
  type        = string
  default     = "512Mi"
}

variable "worker_cloud_run_memory" {
  description = "Memory allocation for the Worker Cloud Run service specifically; falls back to cloud_run_memory when unset. Worker ingests granular wearable payloads that api/web don't, so its ceiling sometimes needs to move independently."
  type        = string
  default     = null
}

# ── Container images ─────────────────────────────────────────────────────────
# These four (api, web, worker, migrator) are bootstrap placeholders, not the
# images we run. A Cloud Run resource cannot be created without a pullable image,
# but the app images do not exist yet on a first apply — hence the hello-world
# default. Every one of those resources then carries lifecycle.ignore_changes on
# its image (deployments/cloud_run.tf), because the deploy workflows re-point them
# on each release via `gcloud run deploy/jobs update --image`.
#
# So editing these values changes nothing once a resource exists, and no tfvars
# file sets them. Leave them at the default; to change a deployed image, deploy.
variable "api_container_image" {
  description = "Bootstrap image seeding the API service's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "web_container_image" {
  description = "Bootstrap image seeding the Web service's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Cloud SQL Configuration
variable "cloud_sql_edition" {
  description = "Cloud SQL edition (ENTERPRISE or ENTERPRISE_PLUS)"
  type        = string
  default     = "ENTERPRISE"
}

variable "cloud_sql_tier" {
  description = "Cloud SQL machine tier (db-f1-micro for dev, db-custom-2-7680 for prod)"
  type        = string
  default     = "db-f1-micro"
}

variable "cloud_sql_disk_size_gb" {
  description = "Cloud SQL disk size in GB"
  type        = number
  default     = 10
}

variable "cloud_sql_ha_enabled" {
  description = "Enable high availability for Cloud SQL (REGIONAL availability type)"
  type        = bool
  default     = false
}

variable "cloud_sql_deletion_protection" {
  description = "Enable deletion protection for Cloud SQL instance"
  type        = bool
  default     = false
}

variable "cloud_sql_public_ip_enabled" {
  description = "Enable public IP for Cloud SQL (should be false; use Cloud SQL Auth Proxy)"
  type        = bool
  default     = false
}

# The AI pipeline's scheduled job (digest generation). Off by default — it calls MedGemma, so
# it belongs only in environments where the model is deployed (dev today; prod's medgemma_image
# is empty).
variable "enable_pipeline_jobs" {
  description = "Create the AI pipeline Cloud Run job + its hourly Cloud Scheduler trigger. Enable only where MedGemma is deployed"
  type        = bool
  default     = false
}

variable "pipeline_jobs_container_image" {
  description = "Bootstrap image seeding the pipeline job's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# The Google Health webhook receiver — the pipeline's public-ingress notification endpoint.
# Requires enable_pubsub (it publishes to the realtime topic).
variable "enable_webhook_receiver" {
  description = "Create the webhook receiver Cloud Run service (public ingress, secret-authenticated). Requires enable_pubsub"
  type        = bool
  default     = false
}

variable "webhook_receiver_container_image" {
  description = "Bootstrap image seeding the webhook receiver's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Bootstrap placeholders — see the container images note above api_container_image.
variable "migrator_container_image" {
  description = "Bootstrap image seeding the DB migrator Job's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "worker_container_image" {
  description = "Bootstrap image seeding the Worker service's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Storage Configuration
variable "storage_location" {
  description = "GCS bucket location (US, EU, ASIA, or specific region)"
  type        = string
  default     = "EU"
}

variable "api_custom_domain" {
  description = "Custom domain for the API Cloud Run service (e.g. api.carditrack.com)"
  type        = string
  default     = ""
}

variable "web_custom_domain" {
  description = "Custom domain for the Web Cloud Run service (e.g. app.carditrack.com)"
  type        = string
  default     = ""
}

variable "webhook_custom_domain" {
  description = "Custom domain for the health webhook receiver, fronted by the same GCLB + Cloud Armor WAF as api/web (e.g. webhook.carditrack.com)"
  type        = string
  default     = ""
}

variable "storage_class" {
  description = "GCS storage class (STANDARD, NEARLINE, COLDLINE, ARCHIVE)"
  type        = string
  default     = "STANDARD"
}

# APM Configuration (logs + traces shipping)
variable "apm_engine" {
  description = "APM backend for API/Web/Worker (must match an engine in ApmProviderRegistry, e.g. BetterStack); empty string disables shipping"
  type        = string
  default     = "BetterStack"
}

variable "apm_mobile_engine" {
  description = "Mobile monitoring engine stamped into app builds (must match an engine in the app's MobileApm registry, e.g. Datadog); empty string disables monitoring"
  type        = string
  default     = "Datadog"
}

variable "apm_metrics_enabled" {
  description = "Export OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql, GenAI) from API/Web/Worker to the APM backend. Off by default: metrics bill as custom metrics and stream around the clock"
  type        = bool
  default     = false
}

# Logging + tracing volume, per service. Objects (not one shared scalar) so a single
# service can be turned up for an investigation without touching the other two; every
# attribute is optional, so `{ api = "Debug" }` leaves Web and Worker on the default.
variable "log_minimum_level" {
  description = "Serilog root minimum level per service (Serilog__MinimumLevel__Default). Warning by default — everything below it never reaches Cloud Logging or the APM sink. Raising a service raises both, since the APM sink inherits this level unless Apm__MinimumLogLevel holds it at a stricter level, so treat a raise as an ingest-spend change. Note: dropping a service below Information also needs Logging__LogLevel__Default, which filters ahead of Serilog"
  type = object({
    api    = optional(string, "Warning")
    web    = optional(string, "Warning")
    worker = optional(string, "Warning")
  })
  default = {}

  validation {
    # A typo here reaches the app as a bad Serilog level; fail the plan instead.
    condition = alltrue([
      for level in [var.log_minimum_level.api, var.log_minimum_level.web, var.log_minimum_level.worker] :
      contains(["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"], level)
    ])
    error_message = "log_minimum_level values must be one of: Verbose, Debug, Information, Warning, Error, Fatal."
  }
}

variable "traces_sample_ratio" {
  description = "OTel trace head-sampling ratio per service (Apm__TracesSampleRatio), 0.0-1.0. 1.0 by default — full sampling; the main APM ingest cost lever alongside apm_metrics_enabled"
  type = object({
    api    = optional(number, 1.0)
    web    = optional(number, 1.0)
    worker = optional(number, 1.0)
  })
  default = {}

  validation {
    # The apps clamp out-of-range values; fail the plan instead of deploying a silent clamp.
    condition = alltrue([
      for ratio in [var.traces_sample_ratio.api, var.traces_sample_ratio.web, var.traces_sample_ratio.worker] :
      ratio >= 0.0 && ratio <= 1.0
    ])
    error_message = "traces_sample_ratio values must be between 0.0 and 1.0 inclusive."
  }
}

# Per-device-type pull parameters. Providers differ in how quickly they finalise a day and how
# hard they rate-limit, so cadence belongs to the device type rather than to any one connection.
#
# The list is index-aligned with the DeviceProviders array the apps bind, and the apps read these
# as DeviceProviders__<i>__* env vars — the same positional contract already used for provider
# secrets, which is why element 0 must stay the GoogleHealth (Google Health API) provider.
#
# Beyond cadence, each entry now carries the provider block's whole non-secret identity — the
# HealthApi name, the DeviceTypes (hardware brands) it serves, endpoints and scopes — so deployed
# environments are configured from tfvars, with appsettings.json only a local-dev default
# underneath (env vars override it per element index and per list index).
#
# These bounds are the only guard on a calibrated pull interval: calibration may move a connection
# within [min, max] but never outside it, so widening the range is deliberately a deploy.
variable "device_pull_params" {
  description = "DeviceProviders blocks (identity, DeviceType mapping, endpoints, and pull cadence), index-aligned with the DeviceProviders array (DeviceProviders__<i>__*). Element 0 must be the GoogleHealth provider"
  type = list(object({
    provider     = string       # HealthApi enum name, e.g. "GoogleHealth"
    device_types = list(string) # DeviceType enum names this API serves, e.g. ["Fitbit", "GooglePixelWatch"]

    # Non-secret OAuth/API endpoints. Null leaves the appsettings value in effect.
    authorization_url    = optional(string)
    token_url            = optional(string)
    api_base_url         = optional(string)
    scopes               = optional(list(string), [])
    token_lifetime_hours = optional(number)

    # Extra authorize-URL params (e.g. Google's access_type=offline) and the ones sent only
    # while no refresh token is banked (prompt=consent).
    additional_authorization_params    = optional(map(string), {})
    first_consent_authorization_params = optional(map(string), {})

    sync_lookback_days        = optional(number, 3)
    backfill_days             = optional(number, 90)
    backfill_chunk_days       = optional(number, 7)
    audit_lookback_days       = optional(number, 14)
    min_pull_interval_minutes = optional(number, 30)
    max_pull_interval_minutes = optional(number, 1440)
    max_requests_per_second   = optional(number, 0)
    dormancy_threshold_pulls  = optional(number, 0)
    dormancy_backoff_factor   = optional(number, 2.0)
  }))
  default = [{ provider = "GoogleHealth", device_types = ["Fitbit", "GooglePixelWatch"] }]

  validation {
    # Mirrors AddGoogleHealthProvider's startup check. Catching it in the plan beats deploying a
    # revision that binds Google's credentials to the wrong provider and then crash-loops.
    condition     = length(var.device_pull_params) > 0 && lower(var.device_pull_params[0].provider) == "googlehealth"
    error_message = "device_pull_params[0] must be the GoogleHealth provider — the apps bind provider config by index."
  }

  validation {
    # Mirrors the app's startup check: an API with no device types can never be reached.
    condition = alltrue([
      for p in var.device_pull_params : length(p.device_types) > 0
    ])
    error_message = "device_pull_params: every entry must list at least one device_type it serves."
  }

  validation {
    # Mirrors the app's double-claim check — first-match resolution must not depend on order.
    condition = length(distinct(flatten([
      for p in var.device_pull_params : [for t in p.device_types : lower(t)]
      ]))) == length(flatten([
      for p in var.device_pull_params : p.device_types
    ]))
    error_message = "device_pull_params: a device_type may be served by exactly one provider entry."
  }

  validation {
    # An inverted range would pin every connection of that type to one end of it.
    condition = alltrue([
      for p in var.device_pull_params :
      p.min_pull_interval_minutes > 0 &&
      p.min_pull_interval_minutes <= p.max_pull_interval_minutes
    ])
    error_message = "device_pull_params: min_pull_interval_minutes must be positive and no greater than max_pull_interval_minutes."
  }

  validation {
    # A factor of 1 or less never widens the interval, so backoff would silently do nothing.
    condition = alltrue([
      for p in var.device_pull_params :
      p.dormancy_threshold_pulls == 0 || p.dormancy_backoff_factor > 1
    ])
    error_message = "device_pull_params: dormancy_backoff_factor must exceed 1 when dormancy_threshold_pulls is set."
  }

  validation {
    # The audit exists to see past the routine window; narrower makes it pointless.
    condition = alltrue([
      for p in var.device_pull_params :
      p.audit_lookback_days >= p.sync_lookback_days
    ])
    error_message = "device_pull_params: audit_lookback_days must be at least sync_lookback_days."
  }

  validation {
    # Mirrors the app's semantics exactly: 0 disables backfill (negative would too, but only by
    # accident — reject it here), and a live horizon needs a positive chunk or the backfill is
    # enabled yet unable to advance.
    condition = alltrue([
      for p in var.device_pull_params :
      p.backfill_days >= 0 && (p.backfill_days == 0 || p.backfill_chunk_days > 0)
    ])
    error_message = "device_pull_params: backfill_days must be >= 0 (0 disables), and backfill_chunk_days must be positive when backfill_days is set."
  }
}

# Pub/Sub Configuration (real-time messaging)
variable "enable_pubsub" {
  description = "Enable Cloud Pub/Sub for real-time messaging (production only)"
  type        = bool
  default     = false
}

# Firebase Cloud Messaging — push notification sending credentials. Provisioning ahead of
# the Phase 3 send path; see infrastructure/deployments/firebase.tf and
# docs/technical/notification_engine.md §16.
variable "enable_push_notifications" {
  description = "Enable Firebase Cloud Messaging (FCM) for push notification sending"
  type        = bool
  default     = false
}

# Networking
# Phase 2 of the MedGemma IAM change. Every service now runs PRIVATE_RANGES_ONLY egress, so nothing
# routes through Cloud NAT and it is a fixed ~£24/month charge for an idle gateway. Kept true by
# default so the identity/egress migration applies with the gateway still in place; flip to false in
# an environment's tfvars as a second, separately-verifiable apply. Read the note in
# deployments/networking.tf first — in particular the egress-IP allowlist check.
variable "enable_cloud_nat" {
  description = "Provision Cloud NAT and its router. Needed only while some service uses ALL_TRAFFIC egress; set false to retire the gateway once the PRIVATE_RANGES_ONLY migration is verified"
  type        = bool
  default     = true
}

# Memorystore for Redis (distributed cache: OAuth PKCE state, report cache)
variable "enable_redis" {
  description = "Provision a Memorystore for Redis instance and bind it to the API. With this off the API has no distributed cache, so device linking fails whenever the OAuth callback lands on a different Cloud Run instance"
  type        = bool
  default     = false
}

variable "redis_tier" {
  description = "Memorystore service tier (BASIC for a standalone instance, STANDARD_HA for primary/replica)"
  type        = string
  default     = "BASIC"
}

variable "redis_memory_size_gb" {
  description = "Memorystore capacity in GB"
  type        = number
  default     = 1
}

variable "redis_version" {
  description = "Memorystore Redis engine version"
  type        = string
  default     = "REDIS_7_2"
}

# Platform Audit Logging
#
# Renamed from enable_hipaa_compliance. The old name overstated what this does: it turns on
# Cloud SQL audit flags and routes Cloud Logging's platform audit trail to a retained bucket.
# That is infrastructure activity — who connected to the database, who deployed a revision.
# It does not record which caregiver read which wearer's health data; nothing does yet, and
# that record is an application concern. The old name also implied a HIPAA posture the
# service does not currently hold — see docs/solution_manifest.md.
variable "enable_platform_audit_logging" {
  description = "Enable Cloud SQL audit flags and the platform audit log sink"
  type        = bool
  default     = false
}

# 90 days covers operational forensics, which is what a platform audit trail is for.
# HIPAA's §164.316(b)(2) six-year retention applies to compliance documentation and to
# application-level PHI access records — neither of which this sink carries — and only once
# HIPAA attaches at all. Raising this is a cost decision (COLDLINE, immutable retention
# policy), not a default to drift into.
variable "audit_retention_days" {
  description = "Platform audit log retention in days"
  type        = number
  default     = 90
}

# Cloud Run OOM Alerting — see infrastructure/deployments/alerting.tf for the manual Slack
# setup step this depends on.
variable "enable_oom_alerting" {
  description = "Create the Cloud Run OOM log-based metric, alert policy, and email notification channels"
  type        = bool
  default     = true
}

variable "alert_notification_emails" {
  description = "Email addresses notified when a Cloud Run container is OOM-killed"
  type        = list(string)
  default     = []
}

variable "enable_slack_alerts" {
  description = "Attach the Slack notification channel to the OOM alert policy (requires alert_slack_channel_id, provisioned via a one-time manual GCP Console Slack OAuth step)"
  type        = bool
  default     = false
}

variable "alert_slack_channel_id" {
  description = "Numeric ID of a Slack notification channel already created via the manual Console OAuth step. Empty disables Slack regardless of enable_slack_alerts"
  type        = string
  default     = ""
}

# Labels
variable "additional_labels" {
  description = "Additional labels to apply to all resources"
  type        = map(string)
  default     = {}
}

# Networking
variable "subnet_cidr" {
  description = "CIDR range for the primary subnet"
  type        = string
  default     = "10.0.0.0/24"
}

# MedGemma (Ollama)
variable "medgemma_image" {
  description = "Container image for MedGemma (empty string disables the service)"
  type        = string
  default     = ""
}

variable "medgemma_cpu" {
  description = "CPU allocation for the MedGemma Cloud Run service"
  type        = string
  default     = "8"
}

variable "medgemma_memory" {
  description = "Memory allocation for the MedGemma Cloud Run service"
  type        = string
  default     = "16Gi"
}

# Kept separate from cloud_run_min_instances so prod can keep a warm API/Web/Worker without
# also paying for a warm 8 vCPU / 16 Gi inference box. The default stays 0: an environment that
# has not decided to spend that should not start doing so by inheriting it. dev.tfvars opts in,
# because MedGemma is on a latency-sensitive path there — the Dashboard status line generates
# inside the caregiver's request — and a warm instance keeps both the model and its prefix cache
# loaded between calls.
variable "medgemma_min_instances" {
  description = "Minimum number of MedGemma instances (0 scales to zero between requests)"
  type        = number
  default     = 0
}

# Ceiling for a MedGemma call from the API. Sized for a cold start: the startup probe alone
# allows up to 150s (30s initial delay + 12 x 10s), and generation follows it.
variable "medgemma_timeout_seconds" {
  description = "HTTP client timeout the API applies to MedGemma calls"
  type        = number
  default     = 300
}

# ── Public AI provider (reports and chat) ─────────────────────────────────────
# Off-estate by definition, and swappable: changing kind + model + the key secret moves
# reports and chat to another provider without a code change. The medical path is not
# configurable here — it is pinned to the in-VPC MedGemma service in application code.

variable "public_ai_kind" {
  description = "Wire protocol of the public AI provider — Gemini or Anthropic"
  type        = string
  default     = "Gemini"

  validation {
    condition     = contains(["Gemini", "Anthropic"], var.public_ai_kind)
    error_message = "public_ai_kind must be one of: Gemini, Anthropic."
  }
}

variable "public_ai_model" {
  description = "Model identifier passed to the public AI provider"
  type        = string
  default     = "gemini-2.0-flash"
}

# Null keeps the provider's documented default endpoint, which is what dev and prod use.
# Set it to reach a gateway or a regional endpoint (e.g. Vertex AI) instead.
variable "public_ai_base_url" {
  description = "Override for the public AI provider endpoint; null uses the per-kind default"
  type        = string
  default     = null
}

variable "public_ai_timeout_seconds" {
  description = "HTTP client timeout the API applies to public AI calls"
  type        = number
  default     = 60
}

# A multi-member report is the longest output we ask a public model for; a low ceiling
# truncates it mid-sentence with no error to catch.
variable "public_ai_max_output_tokens" {
  description = "Upper bound on a single public AI completion"
  type        = number
  default     = 16000
}

# Named separately from the provider so a swap can point at a new secret without
# destroying and recreating the existing one. Defaults to the secret already in place.
variable "public_ai_api_key_secret_id" {
  description = "Secret Manager secret holding the public AI API key; null uses the gemini-api-key secret"
  type        = string
  default     = null
}
