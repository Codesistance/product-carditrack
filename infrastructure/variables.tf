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
  description = "Export OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql) from API/Web/Worker to the APM backend. Off by default: metrics bill as custom metrics and stream around the clock"
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
# secrets, which is why element 0 must stay the Fitbit (Google Health API) provider.
#
# These bounds are the only guard on a calibrated pull interval: calibration may move a connection
# within [min, max] but never outside it, so widening the range is deliberately a deploy.
variable "device_pull_params" {
  description = "Pull cadence bounds and provider rate ceiling per device type, index-aligned with the DeviceProviders array (DeviceProviders__<i>__*). Element 0 must be Fitbit"
  type = list(object({
    provider                  = string
    sync_lookback_days        = optional(number, 3)
    audit_lookback_days       = optional(number, 14)
    min_pull_interval_minutes = optional(number, 30)
    max_pull_interval_minutes = optional(number, 1440)
    max_requests_per_second   = optional(number, 0)
    dormancy_threshold_pulls  = optional(number, 0)
    dormancy_backoff_factor   = optional(number, 2.0)
  }))
  default = [{ provider = "Fitbit" }]

  validation {
    # Mirrors AddFitbitProvider's startup check. Catching it in the plan beats deploying a
    # revision that binds Google's credentials to the wrong provider and then crash-loops.
    condition     = length(var.device_pull_params) > 0 && lower(var.device_pull_params[0].provider) == "fitbit"
    error_message = "device_pull_params[0] must be the Fitbit provider — the apps bind provider config by index."
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
}

# Pub/Sub Configuration (real-time messaging)
variable "enable_pubsub" {
  description = "Enable Cloud Pub/Sub for real-time messaging (production only)"
  type        = bool
  default     = false
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
# also paying for a warm 8 vCPU / 16 Gi inference box. Raise to 1 once MedGemma is on a
# latency-sensitive path; until then a cold start is the cheaper trade.
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
