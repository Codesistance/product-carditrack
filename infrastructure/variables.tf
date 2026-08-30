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

# The AI pipeline's scheduled job (digest generation). Off by default — it calls the shared
# MedGemma service, so it belongs only in environments wired to it: invoker grants in
# common.tfvars plus a real URL secret (dev today; prod is not wired yet).
variable "enable_pipeline_jobs" {
  description = "Create the AI pipeline Cloud Run job + its hourly Cloud Scheduler trigger. Enable only where the environment is wired to the shared MedGemma service"
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
    # The pipeline tier (jobs + aggregator). Absent until now, which did not mean "unsampled" —
    # it meant those services fell through to ApmOptions.TracesSampleRatio's 0.2 default and
    # quietly dropped four traces in five while every other service ran at 1.0.
    pipeline = optional(number, 1.0)
  })
  default = {}

  validation {
    # The apps clamp out-of-range values; fail the plan instead of deploying a silent clamp.
    condition = alltrue([
      for ratio in [var.traces_sample_ratio.api, var.traces_sample_ratio.web, var.traces_sample_ratio.worker, var.traces_sample_ratio.pipeline] :
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

# Dev-only test-push endpoint (notification_engine.md §13)
variable "enable_dev_push_token" {
  description = "Provision Dev:PushTokenKey and bind it to the API, which is what makes POST /api/v1/dev/push exist at all. An explicit opt-in rather than a derived `environment != prod`: the endpoint sends a real push to any user with no authenticated caller, so turning it on should be a reviewable line in one tfvars file, not a consequence of how an environment happens to be named"
  type        = bool
  default     = false

  validation {
    # Belt and braces alongside the app's own DeploymentInfo check, which already refuses to
    # route the controller in prod. This stops the secret and its binding from ever being
    # created there, so the two guards fail independently rather than sharing one assumption.
    condition     = !(var.enable_dev_push_token && var.environment == "prod")
    error_message = "enable_dev_push_token must stay false in prod — the dev test-push endpoint is never provisioned there."
  }
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

# MedGemma public-exposure alerting. On by default because it is the only control standing between
# an accidental allUsers grant and a publicly invocable medical model — this project has no
# organization, so the org policy that would have prevented it outright cannot exist. Reuses the
# notification channels above; self-disables where MedGemma is not deployed.
# Continuous watch on the public domains' TLS certificates. On by default: a managed certificate
# that stops renewing gives no signal at all until it expires, and app.dev.carditrack.com proved
# that gap is real — it lapsed on 2026-08-07 and went unnoticed for six days.
variable "enable_cert_expiry_alerting" {
  description = "Create uptime checks for the configured public domains and alert when a TLS certificate nears expiry"
  type        = bool
  default     = true
}

variable "cert_expiry_alert_days" {
  description = "Fire when a public domain's TLS certificate has fewer than this many days left. Keep below Google's ~30-day managed-certificate renewal window so a certificate mid-renewal does not alert"
  type        = number
  default     = 20
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

# Ceiling for a MedGemma call from the API, worker and pipeline jobs alike
# (AI__Private__TimeoutSeconds / AI__Providers__0__TimeoutSeconds in main.tf). The shared GPU
# service's own request timeout is derived from its equivalent variable in common/variables.tf,
# which must stay longer than this client-side ceiling. Nothing else derives from it now: this
# was shared with the rewrite Cloud Run service until that service was deleted, and the rewrite
# slot's client budget comes from rewrite_ai_timeout_seconds, which is its Vertex kind's own much
# shorter number.
#
# Raised from 300s to 900s on 2026-08-21. Datadog logs from pipeline-jobs (dev, 2026-08-21
# 16:02-16:27 UTC) showed the 300s ceiling routinely hit under real load — not because a single
# generation is that slow (measured p95 was 216.3s, docs/technical/medgemma_serving_architecture.md
# §1), but because an abandoned call does not stop Ollama from finishing it: Cloud Run's
# max_instance_request_concurrency = 1 keeps the one instance "busy" until the orphaned generation
# actually returns a response, so the next caller's fresh request queues or 429s behind work nobody
# is waiting for any more. In that window two calls timed out at 300s and then took 642-643s each to
# fail on retry, and a later call took 166s to return successfully for a prompt that had earlier
# taken 25s server-side — all the same pileup, not slower inference. 900s gives a real generation
# (including one queued behind a still-finishing prior call) room to return an honest answer instead
# of being abandoned and adding to the backlog, while staying well inside the pipeline jobs' own
# 3600s execution timeout.
#
# Does not touch the Dashboard hero card's own budget (PrivateAiSettings.CurrentStatusBudgetSeconds,
# 25s, unaffected by this variable) — that path is deliberately fail-fast because the mobile client
# gives up at 30s regardless of what the server does.
variable "medgemma_timeout_seconds" {
  description = "HTTP client timeout the API, worker and pipeline jobs apply to MedGemma calls"
  type        = number
  default     = 900
}

# The window a MedGemma call has to work in — prompt and completion are spent out of the same one.
# Sized here rather than left to Ollama's own default (4096), which is a chat-turn window: the
# clinical prompts carry a day of readings, the family's questionnaire answers and the reply schema,
# and at 4096 a digest ran out of room part-way through writing its first field, arriving as JSON
# cut mid-token. The cost of raising it is KV cache, which scales with this number, so it stays
# a variable — an environment under memory pressure lowers it here.
variable "medgemma_context_tokens" {
  description = "Context window (num_ctx) for MedGemma calls — prompt and completion share it"
  type        = number
  default     = 8192

  validation {
    condition     = var.medgemma_context_tokens > 0
    error_message = "medgemma_context_tokens must be greater than zero."
  }

  # Checked here as well as in AiServiceExtensions: the app refusing to boot is a correct
  # outcome but a late one — by then the revision is deployed and the old one is gone.
  validation {
    condition     = var.medgemma_context_tokens > var.medgemma_max_output_tokens
    error_message = "medgemma_context_tokens must exceed medgemma_max_output_tokens, or no room is left for a prompt."
  }
}

# Several times what any clinical prompt actually asks for — a digest is a few sentences and some
# short fields. Deliberately generous: this ceiling is not where brevity is enforced (the prompt and
# the reply schema do that, and an over-long reply is rejected downstream on its merits), and a
# ceiling that bites produces truncated JSON, which is unreadable rather than merely too long.
variable "medgemma_max_output_tokens" {
  description = "Upper bound on a single MedGemma completion (num_predict), within the context window"
  type        = number
  default     = 2048

  validation {
    condition     = var.medgemma_max_output_tokens > 0
    error_message = "medgemma_max_output_tokens must be greater than zero."
  }
}

# ── Public AI provider (reports and chat) ─────────────────────────────────────
# Off-estate by definition, and swappable: changing kind + model + the key secret moves
# reports and chat to another provider without a code change. The medical path is not
# configurable here — it is pinned to the in-VPC MedGemma service in application code.

variable "public_ai_kind" {
  description = "Wire protocol of the public AI provider — Gemini, Anthropic or VertexGemini"
  type        = string
  default     = "Gemini"

  validation {
    condition     = contains(["Gemini", "Anthropic", "VertexGemini"], var.public_ai_kind)
    error_message = "public_ai_kind must be one of: Gemini, Anthropic, VertexGemini."
  }
}

# Both live environments override this in their tfvars; the default only catches a new
# environment that forgets to. Retired generations (2.0 retired 2026-06-01 on the Gemini API and
# earlier on Vertex; 2.5 retires ~2026-10-16) must not come back — gemini-3.5-flash works on
# both the Gemini and VertexGemini kinds.
variable "public_ai_model" {
  description = "Model identifier passed to the public AI provider"
  type        = string
  default     = "gemini-3.5-flash"
}

# Null keeps the provider's documented default endpoint, which is what dev and prod use.
# Set it to reach a gateway or a test double — for the Gemini and Anthropic kinds only. For
# VertexGemini the endpoint derives from public_ai_location; main.tf never emits this override
# for that kind, and the app refuses a non-Vertex, non-loopback override at startup, because a
# redirected Vertex call would carry a live ADC bearer token to whatever host it names.
variable "public_ai_base_url" {
  description = "Override for the public AI provider endpoint (Gemini/Anthropic kinds); null uses the per-kind default"
  type        = string
  default     = null
}

# EU-only by validation: the DPIA (v0.11, R-A4/M4) excludes US processing and the global
# endpoint for health-adjacent prompts, so the allowlist is a compliance control expressed as a
# plan gate, not a preference. Widening it is a DPIA change first, a Terraform change second.
variable "public_ai_location" {
  description = "Vertex AI location for the public slot's VertexGemini kind (EU regional endpoint)"
  type        = string
  default     = "europe-west2"

  validation {
    condition     = contains(["europe-west2", "europe-west1", "europe-west4"], var.public_ai_location)
    error_message = "public_ai_location must be an EU region the DPIA allows: europe-west2, europe-west1, europe-west4."
  }
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

# ── Rewrite AI provider (member chat's non-clinical steps) ────────────────────
# Gemini on a Vertex AI EU regional endpoint (DPIA v0.11 row A20, decision D6). There is no kind
# variable any more: the Ollama shape the slot launched with had exactly one host, nothing in this
# stack references it any longer, and the teardown that deletes it follows. The app keeps its
# Ollama kind for local development, where docker-compose supplies the server and none of this
# file applies. The model stays a tfvar so swapping it is an apply.

# Verify a new model against the probe in docs/technical/vertex_ai_setup.md §3 before changing
# this: regional availability, responseJsonSchema support and thinkingBudget 0 are the three
# assumptions the client makes of it.
#
# gemini-3.5-flash-lite (owner decision 2026-08-25, part of standardising the estate on the 3.5
# generation) went out on 2026-08-25 without the §3 probe the comment above required, and the
# probe run 2026-08-30 (after pipeline-jobs started 404ing in Dev) found it 404s in all three
# allowlisted regions — europe-west2, europe-west1 and europe-west4 — it is not served in the EU
# at all. Reverted to the documented fallback, gemini-3.5-flash, which the §3 probe confirms
# served in europe-west2 and europe-west4 (404 in europe-west1). The AiSplitEvaluator comparison
# against the real rewrite prompts still has not run for either model — do that before the next
# swap attempt.
variable "rewrite_ai_model" {
  description = "Model identifier for the rewrite slot's VertexGemini kind"
  type        = string
  default     = "gemini-3.5-flash"
}

# Same EU-only compliance gate as public_ai_location above. Moved from europe-west1 to
# europe-west2 on 2026-08-30 alongside the gemini-3.5-flash-lite revert: gemini-3.5-flash 404s
# in europe-west1 (§3 probe, 2026-08-30) but is served in europe-west2, which also matches
# public_ai_location and the doc's stated region preference (§3: west2 first).
variable "rewrite_ai_location" {
  description = "Vertex AI location for the rewrite slot's VertexGemini kind (EU regional endpoint)"
  type        = string
  default     = "europe-west2"

  validation {
    condition     = contains(["europe-west2", "europe-west1", "europe-west4"], var.rewrite_ai_location)
    error_message = "rewrite_ai_location must be an EU region the DPIA allows: europe-west2, europe-west1, europe-west4."
  }
}

# Vertex answers rewrite-register prompts in seconds — deliberately not medgemma_timeout_seconds,
# whose 300s ceiling exists for CPU-served Ollama cold starts. Member chat's interactive budget
# is what this protects.
variable "rewrite_ai_timeout_seconds" {
  description = "HTTP client timeout for the rewrite slot's VertexGemini kind"
  type        = number
  default     = 60
}

# Rewrite-slot outputs are short (a chat reply, a query plan, three waiting sentences) — far
# below the public slot's report-sized ceiling.
variable "rewrite_ai_max_output_tokens" {
  description = "Upper bound on a single rewrite-slot completion (VertexGemini kind)"
  type        = number
  default     = 8192
}

# The shared MedGemma GPU service's address (infrastructure/common/cloud_run.tf). Set explicitly
# rather than derived: this project issues Cloud Run's hash URL form, not the project-number form,
# so the value cannot be built from parts — and this root cannot read the common stack's state to
# ask it. Read it off an apply of that stack, or from the deploy workflow that writes it.
variable "medgemma_service_url" {
  description = "URL of the shared MedGemma GPU service, seeded into this environment's URL secret"
  type        = string

  validation {
    condition     = can(regex("^https://[a-z0-9-]+[.][a-z0-9-]+[.]run[.]app$", var.medgemma_service_url))
    error_message = "medgemma_service_url must be an https *.run.app URL — the hosts refuse to boot on anything else, and identity-token mode requires a run.app host."
  }
}
