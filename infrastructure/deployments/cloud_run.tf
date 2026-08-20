# Cloud Run Services
# Manages API, Web, Worker, and MedGemma services on Google Cloud Run

# Variables
variable "api_service_name" {
  description = "Name of the API Cloud Run service"
  type        = string
}

variable "web_service_name" {
  description = "Name of the Web Cloud Run service"
  type        = string
}

variable "worker_service_name" {
  description = "Name of the Worker Cloud Run service"
  type        = string
}

variable "medgemma_service_name" {
  description = "Name of the MedGemma Cloud Run service"
  type        = string
}

variable "cloud_run_location" {
  description = "GCP region for Cloud Run services"
  type        = string
}

# The four image variables below seed the initial create only. Each resource sets
# lifecycle.ignore_changes on its image because the deploy workflows re-point them
# per release, so a later change here is a no-op against an existing resource.
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

variable "worker_container_image" {
  description = "Bootstrap image seeding the Worker service's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "migrator_container_image" {
  description = "Bootstrap image seeding the DB migrator Job's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Unlike the four above this one is load-bearing after create: it gates whether the
# service exists at all, so emptying it destroys the service. Only the image value
# is CI/CD-owned once the service exists.
variable "medgemma_image" {
  description = "MedGemma container image — empty disables the service, non-empty enables it; the image value itself seeds the initial create only (CI/CD owns it thereafter)"
  type        = string
  default     = ""
}

variable "api_env_vars" {
  description = "Environment variables for API service"
  type        = map(string)
  default     = {}
}

variable "api_secret_env_vars" {
  description = "Secret Manager-backed env vars for API service (key=env var name, value=secret ID)"
  type        = map(string)
  default     = {}
}

variable "web_env_vars" {
  description = "Environment variables for Web service"
  type        = map(string)
  default     = {}
}

variable "web_secret_env_vars" {
  description = "Secret Manager-backed env vars for Web service (key=env var name, value=secret ID)"
  type        = map(string)
  default     = {}
}

variable "worker_env_vars" {
  description = "Environment variables for Worker service"
  type        = map(string)
  default     = {}
}

variable "worker_secret_env_vars" {
  description = "Secret Manager-backed env vars for Worker service (key=env var name, value=secret ID)"
  type        = map(string)
  default     = {}
}

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
  description = "Memory allocation for the Worker Cloud Run service specifically; falls back to cloud_run_memory when unset."
  type        = string
  default     = null
}

variable "worker_min_instances" {
  description = "Warm instances for the Worker. Must be at least 1 in any environment whose scheduled jobs are expected to run — the Worker's crons live in an in-process timer loop, so an instance that is scaled to zero runs no jobs at all. Set to 0 only to deliberately park the Worker."
  type        = number
  default     = 1
}

variable "cloud_run_min_instances" {
  description = "Minimum number of Cloud Run instances"
  type        = number
  default     = 0
}

variable "cloud_run_max_instances" {
  description = "Maximum number of Cloud Run instances"
  type        = number
  default     = 10
}

variable "cloud_run_labels" {
  description = "Labels for Cloud Run services"
  type        = map(string)
  default     = {}
}

variable "api_custom_domain" {
  description = "Custom domain for the API service (e.g. api.carditrack.com)"
  type        = string
  default     = ""
}

variable "web_custom_domain" {
  description = "Custom domain for the Web service (e.g. app.carditrack.com)"
  type        = string
  default     = ""
}

variable "webhook_custom_domain" {
  description = "Custom domain for the health webhook receiver (e.g. webhook.carditrack.com). When set, the receiver is fronted by the same GCLB + Cloud Armor WAF as api/web instead of taking traffic directly."
  type        = string
  default     = ""
}

# 4, down from 8. With cpu_idle = false the service is billed for its whole instance lifetime at
# this allocation, and a large share of that lifetime is cold start — image pull and Ollama model
# load, which are IO-bound rather than CPU-bound, so the second four vCPU were being paid for
# without shortening the expensive part much. Halving the allocation halves the per-second rate
# for every second the instance is alive.
#
# Inference itself does get slower. The headroom for that is in the timeouts, not here:
# medgemma_timeout_seconds is 300s per attempt against an assessor task budget of 1800s
# (see the job below), and per-member inference failures are swallowed rather than failing the
# run. If assessment throughput becomes the constraint, raise this back before raising cadence —
# a bigger instance for a shorter time beats a smaller one woken more often.
#
# 4 is the floor while medgemma_memory is 16Gi: Cloud Run requires at least 4 vCPU for more than
# 8 GiB and caps 4 vCPU at 16 GiB, so this pair sits on both limits at once. Cutting CPU further
# means cutting memory too, and the model has to fit in memory.
variable "medgemma_cpu" {
  description = "CPU allocation for the MedGemma Cloud Run service. Billed for the full instance lifetime (cpu_idle = false), so this is a direct multiplier on MedGemma spend"
  type        = string
  default     = "4"
}

variable "medgemma_memory" {
  description = "Memory allocation for the MedGemma Cloud Run service"
  type        = string
  default     = "16Gi"
}

variable "medgemma_max_instances" {
  description = "Maximum number of MedGemma instances (Ollama cannot safely multi-instance)"
  type        = number
  default     = 1
}

# Deliberately not cloud_run_min_instances: at 4 vCPU / 16 Gi with cpu_idle = false a warm
# MedGemma instance is the largest line item on the bill, and prod sets that shared variable
# to 1. Scaling to zero trades a cold start (image pull + model load) for paying only while
# an instance is alive. Worth paying for where a request waits on the model — the Dashboard status
# line, which dev opts into via its own tfvars; the caller decides, and the default here does not.
#
# Be precise about what warming buys, because the obvious guess is wrong. It does not make the
# prompt cheap, and nothing will: Gemma 3 uses sliding-window attention, llama.cpp will not restore
# a KV checkpoint under SWA, and so every call reprocesses the whole prompt from token zero however
# long the instance has been up. Measured against dev on 2026-08-13 with min_instances = 1 applied,
# on a warm instance with the model resident: `forcing full prompt re-processing due to lack of
# cache data ... n_swa = 1024`, `cached n_tokens = 0`, on every generation. Shorter prompts are the
# only lever on inference latency here — see the prefix caching note in docs/llm_design.md.
#
# What it does buy is the image pull, the startup probe, and (since OLLAMA_KEEP_ALIVE is set on the
# container below) the ~59s model load. Without that env var the model unloads on Ollama's
# 5-minute idle timer and a warm instance still pays the load between most calls, which is the
# shape this variable had when it was first raised to 1.
#
# The trade is not unconditional in the other direction either, which is the trap worth naming: a
# cold start costs the full allocation for the ~150s the startup probe allows, so N wakes a day
# cost roughly N x 150s of instance time before any inference happens. Past a few hundred wakes a
# day that exceeds what a single always-warm instance would have cost, and scaling to zero becomes
# the more expensive option. At the */5 assessor cadence this variable was on the wrong side of
# that crossover. Revisit it and the scheduler cadences together, never one alone.
variable "medgemma_min_instances" {
  description = "Minimum number of MedGemma instances (0 scales to zero between requests)"
  type        = number
  default     = 0
}

# The caller's deadline, mirrored into this module so the service's own request timeout can be
# derived from it rather than restated. The root owns the value and hands it to the .NET hosts as
# AI__Private__TimeoutSeconds (main.tf); the service timeout below has to stay strictly greater, and
# two independently-edited numbers do not stay in a relationship.
variable "medgemma_timeout_seconds" {
  description = "HTTP client timeout the callers apply to MedGemma calls. The service's own request timeout is derived from this and must remain longer — see the timeout in the medgemma service"
  type        = number
  default     = 300
}

# ── Rewrite (split from MedGemma) ─────────────────────────────────────────────
variable "rewrite_service_name" {
  description = "Name of the Rewrite Cloud Run service"
  type        = string
}

variable "rewrite_cpu" {
  description = "CPU allocation for the Rewrite Cloud Run service"
  type        = string
  default     = "2"
}

variable "rewrite_memory" {
  description = "Memory allocation for the Rewrite Cloud Run service"
  type        = string
  # 8Gi is the floor, not a preference: at 4Gi the instance was killed on every model load —
  # "Memory limit of 4096 MiB exceeded with 4265 MiB used", measured in dev 2026-08-20 (issue
  # #397 follow-up) — so the service crash-looped and never served a single generate. The
  # gemma3:4b-it-qat weights alone are ~3GB before Ollama's runtime, vocab and KV cache.
  # Cloud Run allows up to 8Gi on the 2 vCPU this service runs; more than 8Gi would force
  # 4 vCPU (see medgemma_cpu's comment) and nothing measured asks for it.
  default = "8Gi"
}

variable "rewrite_min_instances" {
  description = "Minimum number of Rewrite instances (0 scales to zero between requests)"
  type        = number
  default     = 0
}

variable "rewrite_max_instances" {
  description = "Maximum number of Rewrite instances"
  type        = number
  default     = 1
}

# Resources
resource "google_cloud_run_v2_service" "api" {
  name     = var.api_service_name
  location = var.cloud_run_location
  ingress  = local.api_web_has_domain ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    service_account = google_service_account.api.email

    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      # PRIVATE_RANGES_ONLY. This was ALL_TRAFFIC so that InsightsController's calls to MedGemma's
      # public *.run.app URL would leave via the VPC and satisfy its internal-only ingress. MedGemma
      # now authorises by IAM instead, so that round trip is unnecessary: the call goes out the
      # normal internet path with an OIDC token attached and is authorised on arrival.
      #
      # What still needs the VPC is RFC1918 only — the Cloud SQL private IP (dev has no public IP at
      # all) and Memorystore. Auth0 and Datadog go direct, which is what allows Cloud NAT to be
      # retired; see the enable_cloud_nat note in networking.tf.
      egress = "PRIVATE_RANGES_ONLY"
    }

    volumes {
      name = "cloudsql"
      cloud_sql_instance {
        instances = [google_sql_database_instance.main.connection_name]
      }
    }

    containers {
      image = var.api_container_image

      dynamic "env" {
        for_each = var.api_env_vars
        iterator = item
        content {
          name  = item.key
          value = item.value
        }
      }

      dynamic "env" {
        for_each = var.api_secret_env_vars
        iterator = item
        content {
          name = item.key
          value_source {
            secret_key_ref {
              secret  = item.value
              version = "latest"
            }
          }
        }
      }

      # The internal enqueue endpoint's GoogleOidc scheme (notification_engine.md §7.2 C4) pins
      # both the audience and the calling service account. Set here rather than in var.api_env_vars
      # because the service account identity comes from this module, not something root main.tf
      # can compute. The digest/assessor jobs run as google_service_account.pipeline (not the
      # default compute SA); when those jobs are disabled the pin stays on the compute SA so the
      # API can still boot — nothing will mint a matching token until the pipeline exists.
      env {
        name  = "Pipeline__Audience"
        value = "${var.project_id}-internal-notifications"
      }

      env {
        name  = "Pipeline__ServiceAccount"
        value = var.enable_pipeline_jobs ? google_service_account.pipeline[0].email : "${data.google_project.current.number}-compute@developer.gserviceaccount.com"
      }

      volume_mounts {
        name       = "cloudsql"
        mount_path = "/cloudsql"
      }

      resources {
        limits = {
          cpu    = var.cloud_run_cpu
          memory = var.cloud_run_memory
        }
      }
    }

    scaling {
      min_instance_count = var.cloud_run_min_instances
      max_instance_count = var.cloud_run_max_instances
    }
  }

  labels = var.cloud_run_labels
  # client/client_version are provenance only — a record of which tool last wrote
  # the resource. CI deploys with `gcloud run deploy`, which stamps client=gcloud,
  # and an apply stamps client=terraform straight back, so every plan after a
  # release wanted to change all of them. Nothing functional rides on the value.
  # Every Cloud Run resource below repeats this list for the same reason.
  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.app_secrets,
    google_secret_manager_secret_version.db_connection_string,
    google_secret_manager_secret_version.gemini_api_key,
    google_secret_manager_secret_version.medgemma_service_url,
    google_secret_manager_secret_version.rewrite_service_url,
    google_secret_manager_secret_iam_member.medgemma_url_accessor,
    google_secret_manager_secret_version.redis_connection_string,
    google_secret_manager_secret_version.redis_ca,
    google_secret_manager_secret_iam_member.redis_connection_string_accessor,
    google_secret_manager_secret_iam_member.redis_ca_accessor,
    # Not the api_* IAM members directly: Cloud Run validates secret_key_ref against this
    # service's runtime identity when it creates the revision, and those grants are eventually
    # consistent. See the barrier's comment in service_accounts.tf — this is the dependency that
    # actually prevents the "Permission denied on secret ... for Revision service account" failure.
    time_sleep.api_iam_propagation,
  ]
}

resource "google_cloud_run_v2_service" "web" {
  name     = var.web_service_name
  location = var.cloud_run_location
  ingress  = local.api_web_has_domain ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    # Its own identity, not the shared compute SA — see service_accounts.tf. This service is the
    # public one, and the compute SA it used to run as can read the device-token encryption key.
    service_account = google_service_account.web.email

    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      egress = "PRIVATE_RANGES_ONLY"
    }

    # GCS volumes require the gen2 execution environment; set it explicitly so
    # deploys don't depend on Cloud Run's auto-selection.
    execution_environment = "EXECUTION_ENVIRONMENT_GEN2"

    # Declared dpkeys-first to match the order the Cloud Run API reports back.
    # volumes and volume_mounts are ordered lists in the provider schema, so a
    # config order that disagrees with the API's replans the whole block on every
    # run — the diff never converges no matter how often it is applied.
    volumes {
      name = "dpkeys"
      gcs {
        bucket = google_storage_bucket.dataprotection_keys.name
      }
    }

    volumes {
      name = "cloudsql"
      cloud_sql_instance {
        instances = [google_sql_database_instance.main.connection_name]
      }
    }

    containers {
      image = var.web_container_image

      env {
        name  = "DataProtection__KeysPath"
        value = "/var/dpkeys"
      }

      dynamic "env" {
        for_each = var.web_env_vars
        iterator = item
        content {
          name  = item.key
          value = item.value
        }
      }

      dynamic "env" {
        for_each = var.web_secret_env_vars
        iterator = item
        content {
          name = item.key
          value_source {
            secret_key_ref {
              secret  = item.value
              version = "latest"
            }
          }
        }
      }

      volume_mounts {
        name       = "dpkeys"
        mount_path = "/var/dpkeys"
      }

      volume_mounts {
        name       = "cloudsql"
        mount_path = "/cloudsql"
      }

      resources {
        limits = {
          cpu    = var.cloud_run_cpu
          memory = var.cloud_run_memory
        }
      }

      # Explicit, not the GCP default (tcp_socket, 240s timeout, failure_threshold 1 — a single
      # 4-minute attempt with no retries). That default turned one transient cold-start blip
      # (five services deploying against the same Cloud SQL instance within seconds of each
      # other) into an outright deploy failure on 2026-08-10. Same period as medgemma's probe
      # below, higher failure_threshold: many short retries recover from a blip that a single
      # long one can't.
      startup_probe {
        tcp_socket {}
        period_seconds    = 10
        timeout_seconds   = 10
        failure_threshold = 18
      }
    }

    scaling {
      min_instance_count = var.cloud_run_min_instances
      max_instance_count = var.cloud_run_max_instances
    }
  }

  labels = var.cloud_run_labels
  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.app_secrets,
    google_secret_manager_secret_version.db_connection_string,
    # Not the web_* IAM members directly: Cloud Run validates the apm-data secret_key_ref and the
    # GCS volume against this service's runtime identity when it creates the revision, and those
    # grants are eventually consistent. See the barrier's comment in service_accounts.tf. The
    # barrier already covers web_dpkeys, which is why the removed compute-SA grant on the key-ring
    # bucket is not replaced here by a direct reference.
    time_sleep.web_iam_propagation,
  ]
}

# ── DB Migrator Job ──────────────────────────────────────────────────────────
# Runs EF Core migrations against the private DB via Cloud SQL Auth Proxy socket.
# Executed once per deploy by the CI pipeline; exits when migrations are complete.
# The image is owned by CI (`gcloud run jobs update --image`), not Terraform — the
# variable default only bootstraps the initial create, so image changes are ignored
# here exactly as they are for the api/web/worker/medgemma services.
resource "google_cloud_run_v2_job" "migrator" {
  name     = "${var.api_service_name}-migrator"
  location = var.cloud_run_location
  client   = "terraform"

  template {
    template {
      max_retries = 1

      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "PRIVATE_RANGES_ONLY"
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [google_sql_database_instance.main.connection_name]
        }
      }

      containers {
        image = var.migrator_container_image

        env {
          name = "ConnectionStrings__DefaultConnection"
          value_source {
            secret_key_ref {
              secret  = google_secret_manager_secret.db_connection_string.secret_id
              version = "latest"
            }
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }

        resources {
          limits = {
            cpu    = "1"
            memory = "512Mi"
          }
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.db_connection_string,
  ]
}

resource "google_cloud_run_v2_service" "worker" {
  name     = var.worker_service_name
  location = var.cloud_run_location
  ingress  = "INGRESS_TRAFFIC_INTERNAL_ONLY"
  client   = "terraform"

  template {
    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      egress = "PRIVATE_RANGES_ONLY"
    }

    volumes {
      name = "cloudsql"
      cloud_sql_instance {
        instances = [google_sql_database_instance.main.connection_name]
      }
    }

    containers {
      image = var.worker_container_image

      dynamic "env" {
        for_each = var.worker_env_vars
        iterator = item
        content {
          name  = item.key
          value = item.value
        }
      }

      dynamic "env" {
        for_each = var.worker_secret_env_vars
        iterator = item
        content {
          name = item.key
          value_source {
            secret_key_ref {
              secret  = item.value
              version = "latest"
            }
          }
        }
      }

      volume_mounts {
        name       = "cloudsql"
        mount_path = "/cloudsql"
      }

      # cpu_idle = false, unlike every other service here. The Worker is not a request handler:
      # its whole job is CronBackgroundService's timer loop (wearable sync, baselines, statistical
      # alerts, inactivity detection, notification dispatch). Cloud Run's default request-based
      # CPU allocation throttles an instance to near-zero between requests, and nothing ever sends
      # this service one — so the loop was being starved and the crons simply did not fire. That
      # is the shared root cause behind alerts never arriving, push never being delivered, and
      # baselines never being computed: not one of those jobs was running.
      resources {
        limits = {
          cpu    = var.cloud_run_cpu
          memory = coalesce(var.worker_cloud_run_memory, var.cloud_run_memory)
        }
        cpu_idle          = false
        startup_cpu_boost = true
      }

      # Explicit, not the GCP default (tcp_socket, 240s timeout, failure_threshold 1 — a single
      # 4-minute attempt with no retries). That default turned one transient cold-start blip
      # (five services deploying against the same Cloud SQL instance within seconds of each
      # other) into an outright deploy failure on 2026-08-10. http_get against the Worker's own
      # /healthz (see Program.cs) rather than a bare TCP check, since the Worker exposes one.
      startup_probe {
        http_get {
          path = "/healthz"
        }
        period_seconds    = 10
        timeout_seconds   = 10
        failure_threshold = 18
      }
    }

    # Deliberately not cloud_run_min_instances, which is 0 outside prod: a scheduled worker with
    # no warm instance has nowhere to run its schedule, and in dev the instance was being reclaimed
    # and taking every cron with it. One always-on instance is what makes this a worker rather than
    # an idle deployment. Max is pinned alongside it — CronBackgroundService holds its schedule in
    # process with no leader election, so a second instance would run every job a second time.
    scaling {
      min_instance_count = var.worker_min_instances
      max_instance_count = 1
    }
  }

  labels = var.cloud_run_labels
  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.app_secrets,
    google_secret_manager_secret_version.db_connection_string,
    google_secret_manager_secret_version.medgemma_service_url,
    google_secret_manager_secret_version.rewrite_service_url,
    # The worker runs as the default compute SA — see the barrier's comment in
    # service_accounts.tf for the revision-rejection failure this ordering prevents.
    time_sleep.compute_sa_secret_propagation,
  ]
}

# ── MedGemma (Ollama) ─────────────────────────────────────────────────────────
# Authorised by IAM, not by network position. URL written to Secret Manager by CI/CD after each
# deployment.
#
# This was INGRESS_TRAFFIC_INTERNAL_ONLY with an allUsers invoker, which made the *route* the only
# control: no caller authenticated, so anything that could reach the VPC could run inference. The
# swap to INGRESS_TRAFFIC_ALL plus a named-identity invoker binding means callers now present a
# Google-signed OIDC token (MedGemmaIdentityTokenHandler) whose audience is this service's URL.
#
# Routable does not mean reachable. Cloud Run enforces run.invoker at the Google front end, so an
# unauthenticated request is rejected before it is dispatched to a container — it cannot trigger a
# cold start, and with cpu_idle = false a cold start is the expensive thing here. Internet
# scanning therefore costs nothing.
#
# What this gave up: internal-only ingress used to contain an IAM mistake. Now IAM is the only
# boundary, so re-adding allUsers — or allAuthenticatedUsers, which reads as restrictive but means
# any Google account anywhere — would expose the model with no network backstop.
#
# This comment used to say the control making that impossible was the
# constraints/iam.allowedPolicyMemberDomains org policy, pending as a follow-up. It is not pending:
# it cannot exist. There is no organization above this project, and Domain Restricted Sharing
# allow-lists Cloud Identity customer IDs — with no organization there is no directory to name, so
# the constraint is meaningless rather than merely unset. VPC Service Controls, the other network
# backstop worth considering, needs an organization too.
#
# So this is an accepted risk, recorded rather than deferred: no platform control prevents the
# grant. What exists instead is detection — see the MedGemma section of alerting.tf, which fires
# on the audit-log entry within minutes. Prevention would need either a Cloud Identity organization
# (which would also unlock VPC-SC and SCC) or, possibly, a project-level IAM deny policy on
# run.services.setIamPolicy. Do not reinstate the org-policy claim above without checking that an
# organization now exists.
resource "google_cloud_run_v2_service" "medgemma" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = var.medgemma_service_name
  location = var.cloud_run_location
  ingress  = "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    scaling {
      min_instance_count = var.medgemma_min_instances
      max_instance_count = var.medgemma_max_instances
    }

    # One request at a time, deliberately. The platform default here was 640 — not a chosen
    # number, just what Cloud Run applies when Terraform stays silent, and a nonsense one for
    # a service that can serve exactly one instance and cannot scale out.
    #
    # 640 does not mean 640 get served; it means 640 get *admitted*. Ollama accepts them,
    # splits the 4 vCPU across however many parallel slots it auto-selected, and every one of
    # them slows down together. That is the shape the measurements show: p50 inference was
    # 15-19s to 08-13, and reached 124s by 08-19 while the container image never changed
    # (same digest since 08-10) and prompt sizes stayed flat. What changed in between is the
    # arrival rate. Requests that then overrun the 300s ceiling die as 504s having consumed
    # five minutes of the one instance — 16 of them on 08-18 alone.
    #
    # At 1, a second concurrent caller is refused in 0ms instead of being let in to make the
    # first one slower. That refusal is not a regression: MedGemmaClient already treats 429 as
    # saturation and backs off in 15s steps honouring Retry-After (PR #383), which is the
    # correct response to "busy" and cannot be the response to a 300s timeout — by then the
    # work is done and thrown away. Trading slow shared failure for fast honest rejection is
    # the whole change.
    #
    # This is a hypothesis with a measurement attached, not a certainty: it predicts p50
    # returns toward ~20s and 504s go to zero, while 429 counts rise and are absorbed by the
    # client's backoff. If p50 does not move, the contention theory is wrong and this reverts
    # to a single number with no other consequence. Raise it only alongside an explicit
    # OLLAMA_NUM_PARALLEL on the container, so the two agree instead of one silently
    # oversubscribing the other.
    max_instance_request_concurrency = 1

    # One minute longer than the caller's deadline, so the client is always the one that gives up
    # first. They were previously both exactly 300s, which made the loser of every timeout
    # arbitrary and cost a real diagnosis: the same failure surfaced as a client
    # TaskCanceledException or a server 504 depending on which side won the race. The client owns
    # the retry decision, so the client must own the deadline.
    #
    # Derived, not restated. The caller's value is medgemma_timeout_seconds, applied as
    # HttpClient.Timeout in AiServiceExtensions; writing a literal here would hold the ordering
    # only until someone changed one of the two numbers.
    timeout = "${var.medgemma_timeout_seconds + 60}s"

    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      egress = "PRIVATE_RANGES_ONLY"
    }

    containers {
      image = var.medgemma_image

      # Keep the model resident for the instance's whole life. Ollama's default unloads it after
      # 5 minutes idle, which at the scheduler cadences above is between most calls — and a reload
      # is not cheap: measured in dev on 2026-08-13, 58.6s from `llama_model_loader: loaded meta
      # data` to `srv llama_server: model loaded`. (Not to be confused with the sub-second
      # `load_duration` Ollama reports when the model was already resident.)
      #
      # That reload is billed like everything else here, because cpu_idle = false means the
      # instance bills its full allocation whether it is inferring, loading, or idle. So unloading
      # saves nothing while an instance is alive; it only adds a minute of paid-for latency to the
      # next caller. On the request path — the Dashboard status line — that minute lands far past
      # both the 25s generation budget and the mobile client's 30s timeout, so the first call after
      # any quiet spell returns no live line at all.
      #
      # Costs memory, not money: 16Gi is reserved for the instance regardless of what is in it.
      env {
        name  = "OLLAMA_KEEP_ALIVE"
        value = "-1"
      }

      # Turn off llama.cpp's host-side prompt cache, which on this model can only ever cost.
      # Gemma 3 uses sliding-window attention and llama.cpp will not restore a KV checkpoint under
      # SWA, so every generation reprocesses the prompt from token zero (docs/llm_design.md). The
      # cache still does all the work of trying: it matches the common prefix by LCP similarity,
      # saves the state, then discards it. Measured in dev on 2026-08-13, per request:
      #
      #   srv  get_availabl: updating prompt cache
      #   srv   prompt_save:  - saving prompt with length 538, total state size = 71.466 MiB
      #   srv        update:  - cache state: 2 prompts, 507.974 MiB (limits: 8192.000 MiB, ...)
      #   srv  get_availabl: prompt cache update took 335.74 ms
      #   slot   operator(): forcing full prompt re-processing due to lack of cache data
      #
      # ~336 ms of every request and ~508 MiB of resident state, for a cache that is never read.
      # LLAMA_ARG_CACHE_RAM is llama.cpp's env equivalent of --cache-ram (0 disables); Ollama
      # passes its own environment through to the llama-server it spawns. The 8192 MiB default in
      # the log above is that flag's, which is what confirms this build honours it.
      #
      # Revisit alongside the SWA note: if the model changes or llama.cpp learns to restore SWA
      # checkpoints, this becomes the wrong setting and the prompt trim stops being the only lever.
      #
      # Verify by absence, not by the boot log — llama.cpp still prints "prompt cache is enabled,
      # size limit: 8192 MiB" even when disabled (ggml-org/llama.cpp#22127). The real signal is
      # that the `prompt_save` and `prompt cache update took` lines stop appearing per request.
      env {
        name  = "LLAMA_ARG_CACHE_RAM"
        value = "0"
      }

      resources {
        limits = {
          cpu    = var.medgemma_cpu
          memory = var.medgemma_memory
        }
        cpu_idle          = false
        startup_cpu_boost = true
      }

      ports {
        container_port = 8080
      }

      startup_probe {
        http_get {
          path = "/"
        }
        initial_delay_seconds = 30
        period_seconds        = 10
        failure_threshold     = 12
      }
    }
  }

  labels = var.cloud_run_labels
  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [google_project_service.run]
}

# Split off the medgemma service (member-chat planning notes, 2026-08-20): same image, own
# instance, so a member-chat malicious-check/query-plan/rewrite call never contends with
# MedGemma's own callers for the one CPU allocation the medgemma service above has. Same
# security posture as medgemma: INGRESS_TRAFFIC_ALL, with IAM (`roles/run.invoker` on the two
# named runtime identities below, no allUsers) as the boundary — callers present a Google-signed
# OIDC token and anything else is rejected at the Google front end. There is no network-level
# backstop here; see the medgemma IAM-alerting note in the accepted-risks record.
resource "google_cloud_run_v2_service" "rewrite" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = var.rewrite_service_name
  location = var.cloud_run_location
  ingress  = "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    scaling {
      min_instance_count = var.rewrite_min_instances
      max_instance_count = var.rewrite_max_instances
    }

    # Same reasoning as medgemma's concurrency = 1 above: Ollama cannot safely multi-instance,
    # and one request at a time is a fast, honest 429 rather than several calls quietly slowing
    # each other down. Revisit alongside the benchmark the rewrite_cpu variable's comment asks for.
    max_instance_request_concurrency = 1

    timeout = "${var.medgemma_timeout_seconds + 60}s"

    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      egress = "PRIVATE_RANGES_ONLY"
    }

    containers {
      image = var.medgemma_image

      # OLLAMA_KEEP_ALIVE follows the scaling shape, for the reason medgemma's copy documents:
      # the override exists to keep a permanently-warm instance from paying the model reload
      # between calls. At the default rewrite_min_instances = 0 the instance dies between calls
      # anyway, so the env var would buy nothing and Ollama's 5-minute idle unload is right. An
      # environment that keeps an instance warm (dev does — issue #397: chat's scale-from-zero
      # outlasted every caller's retry budget) needs the model pinned too, or the warm instance
      # still pays the ~59s reload on the first call after five quiet minutes. Costs memory, not
      # money: the instance's allocation is reserved regardless of what is in it.
      dynamic "env" {
        for_each = var.rewrite_min_instances > 0 ? [1] : []
        content {
          name  = "OLLAMA_KEEP_ALIVE"
          value = "-1"
        }
      }

      env {
        name  = "LLAMA_ARG_CACHE_RAM"
        value = "0"
      }

      # Cap the runner's context window. Left uncapped, Ollama sizes the KV cache for the
      # model's full trained window — 131072 tokens for gemma3 — and that allocation, not the
      # ~3GiB of q4 weights, is what pushed this instance to 8.2GiB and an OOM kill *during
      # inference* even after the memory raise (measured 2026-08-20, issue #397: model loaded,
      # generated 227 tokens, then died at "8209 MiB used" against the 8Gi limit). The rewrite
      # slot's prompts are short and bounded by construction (the guard check, the query plan,
      # and a rewrite of a capped clinical read — see MemberContextComposer's section caps), so
      # 8192 tokens is generous headroom, and the KV saving is what actually makes this service
      # fit its allocation. medgemma's own service deliberately stays uncapped: its clinical
      # prompts carry the full member context and its 16Gi was sized with the full window in.
      env {
        name  = "OLLAMA_CONTEXT_LENGTH"
        value = "8192"
      }

      resources {
        limits = {
          cpu    = var.rewrite_cpu
          memory = var.rewrite_memory
        }
        # true (the default, stated explicitly): unlike medgemma, this service is not forced
        # always-on, so ordinary request-based CPU billing/throttling is the cheaper shape —
        # cpu_idle = false only pays for itself on an instance that has to do work between
        # requests, which this one does not.
        cpu_idle          = true
        startup_cpu_boost = true
      }

      ports {
        container_port = 8080
      }

      startup_probe {
        http_get {
          path = "/"
        }
        initial_delay_seconds = 30
        period_seconds        = 10
        failure_threshold     = 12
      }
    }
  }

  labels = var.cloud_run_labels
  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [google_project_service.run]
}

# Allow unauthenticated access (traffic enters via GCLB + Cloud Armor)
resource "google_cloud_run_v2_service_iam_member" "api_public" {
  name     = google_cloud_run_v2_service.api.name
  location = google_cloud_run_v2_service.api.location
  role     = "roles/run.invoker"
  member   = "allUsers"
}

resource "google_cloud_run_v2_service_iam_member" "web_public" {
  name     = google_cloud_run_v2_service.web.name
  location = google_cloud_run_v2_service.web.location
  role     = "roles/run.invoker"
  member   = "allUsers"
}

# MedGemma invokers — the named identities that replaced allUsers. Exactly the callers that have a
# MedGemma code path: the API (InsightsController) and the digest/assessor jobs. Deliberately not
# the aggregator: it carries AI__Private__* env because it shares the pipeline image, but no code
# path in it constructs the client (NotificationDrainService only drains Pub/Sub and runs syncs).
# Deliberately not the default compute SA either — web, worker and the migrator run as that
# identity and none of them call MedGemma. See service_accounts.tf.
resource "google_cloud_run_v2_service_iam_member" "medgemma_api_invoker" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = google_cloud_run_v2_service.medgemma[0].name
  location = google_cloud_run_v2_service.medgemma[0].location
  role     = "roles/run.invoker"
  member   = local.api_sa
}

resource "google_cloud_run_v2_service_iam_member" "medgemma_pipeline_invoker" {
  count    = var.medgemma_image != "" && var.enable_pipeline_jobs ? 1 : 0
  name     = google_cloud_run_v2_service.medgemma[0].name
  location = google_cloud_run_v2_service.medgemma[0].location
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.pipeline[0].email}"
}

# Rewrite invokers — same two callers as MedGemma (member chat's steps run from the API;
# family-summary rewriting, once it exists, would run from the pipeline jobs the same way the
# assessor's clinical call does today).
resource "google_cloud_run_v2_service_iam_member" "rewrite_api_invoker" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = google_cloud_run_v2_service.rewrite[0].name
  location = google_cloud_run_v2_service.rewrite[0].location
  role     = "roles/run.invoker"
  member   = local.api_sa
}

resource "google_cloud_run_v2_service_iam_member" "rewrite_pipeline_invoker" {
  count    = var.medgemma_image != "" && var.enable_pipeline_jobs ? 1 : 0
  name     = google_cloud_run_v2_service.rewrite[0].name
  location = google_cloud_run_v2_service.rewrite[0].location
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.pipeline[0].email}"
}

# ── Pipeline jobs (AI pipeline — summary generation) ─────────────────────────────────────────
# The AI pipeline's scheduled work runs as a Cloud Run *job*, triggered every quarter hour by
# Cloud Scheduler: each execution regenerates the summaries of whichever members' data has moved
# since their last one, then exits. Gated on enable_pipeline_jobs — the job calls MedGemma, so
# it only exists in environments where the model is deployed.

variable "enable_pipeline_jobs" {
  description = "Create the AI pipeline job + its scheduler. Enable only where MedGemma is deployed"
  type        = bool
  default     = false
}

variable "pipeline_jobs_name" {
  description = "Name of the pipeline jobs Cloud Run job"
  type        = string
  default     = ""
}

variable "pipeline_jobs_container_image" {
  description = "Bootstrap image seeding the pipeline job's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "pipeline_jobs_env_vars" {
  description = "Environment variables for the pipeline job"
  type        = map(string)
  default     = {}
}

variable "pipeline_jobs_secret_env_vars" {
  description = "Secret Manager-backed env vars for the pipeline job (key=env var name, value=secret ID)"
  type        = map(string)
  default     = {}
}

# Half-hourly. This used to match DigestGenerationService's MinimumRegenerationInterval, which
# was 20 minutes; that floor is an hour as of 2026-08-17, so a half-hourly pass now outruns what
# a member's summary can produce and every other one can only no-op or catch a waiver.
#
# Deliberately not moved to hourly in the same change. The floor is not the only thing this
# cadence answers to — the waivers (a problem window, a jump, a baseline divergence, an alert)
# cut through it, and this job is the fallback path that catches them for a member the */5
# assessor has not. Slowing it trades waiver latency for instance cost, and that trade belongs
# with the medgemma_min_instances crossover, which the comment on that variable says to revisit
# alongside the scheduler cadences, never one alone.
#
# The cadence does not multiply *inference* cost — the job skips members whose data has not
# moved (DigestGenerationService's dataChangedAtUtc gate). It does multiply *instance* cost,
# which is the part the previous rationale here missed: any pass that finds even one member to
# regenerate wakes MedGemma, and a cold start pays a multi-GB image pull plus model load
# against a startup probe that allows ~150s (see the medgemma service below), all billed at
# the full CPU allocation. The number of passes per hour is therefore a direct cost lever
# whatever the per-member gating does.
variable "pipeline_jobs_schedule" {
  description = "Cloud Scheduler cron for the digest job. Half-hourly: faster than the hourly regeneration floor on purpose, so a waiver (problem window, jump, baseline divergence, alert) is caught for members the */5 assessor pass has not, without paying a MedGemma cold start every quarter hour"
  type        = string
  default     = "*/30 * * * *"
}

resource "google_service_account" "pipeline_scheduler" {
  count        = var.enable_pipeline_jobs ? 1 : 0
  account_id   = "pipeline-jobs-scheduler"
  display_name = "Cloud Scheduler invoker for the CardiTrack pipeline job"
}

resource "google_cloud_run_v2_job" "pipeline_jobs" {
  count    = var.enable_pipeline_jobs ? 1 : 0
  name     = var.pipeline_jobs_name
  location = var.cloud_run_location
  client   = "terraform"

  template {
    template {
      max_retries = 1

      # A digest pass is ~one CPU-served MedGemma call per due member; the generous timeout
      # covers a large timezone bucket without the execution being killed mid-generation.
      timeout = "3600s"

      service_account = google_service_account.pipeline[0].email

      # PRIVATE_RANGES_ONLY: MedGemma authorises by IAM now, so its calls no longer need to leave
      # via the VPC to be recognised as internal — they carry an OIDC token instead. Only the Cloud
      # SQL private IP still needs the VPC. See the api service above and networking.tf.
      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "PRIVATE_RANGES_ONLY"
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [google_sql_database_instance.main.connection_name]
        }
      }

      containers {
        image = var.pipeline_jobs_container_image
        args  = ["--job", "digest"]

        dynamic "env" {
          for_each = var.pipeline_jobs_env_vars
          iterator = item
          content {
            name  = item.key
            value = item.value
          }
        }

        dynamic "env" {
          for_each = var.pipeline_jobs_secret_env_vars
          iterator = item
          content {
            name = item.key
            value_source {
              secret_key_ref {
                secret  = item.value
                version = "latest"
              }
            }
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }

        resources {
          limits = {
            cpu    = "1"
            memory = "512Mi"
          }
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.db_connection_string,
    # See the barrier's comment in service_accounts.tf.
    time_sleep.pipeline_iam_propagation,
  ]
}

resource "google_cloud_run_v2_job_iam_member" "pipeline_scheduler_invoker" {
  count    = var.enable_pipeline_jobs ? 1 : 0
  name     = google_cloud_run_v2_job.pipeline_jobs[0].name
  location = google_cloud_run_v2_job.pipeline_jobs[0].location
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.pipeline_scheduler[0].email}"
}

resource "google_cloud_scheduler_job" "pipeline_jobs_digest" {
  count            = var.enable_pipeline_jobs ? 1 : 0
  name             = "${var.pipeline_jobs_name}-digest"
  region           = var.cloud_run_location
  schedule         = var.pipeline_jobs_schedule
  time_zone        = "Etc/UTC"
  attempt_deadline = "320s"

  http_target {
    http_method = "POST"
    uri         = "https://run.googleapis.com/v2/projects/${var.project_id}/locations/${var.cloud_run_location}/jobs/${var.pipeline_jobs_name}:run"

    oauth_token {
      service_account_email = google_service_account.pipeline_scheduler[0].email
    }
  }

  depends_on = [
    google_project_service.cloudscheduler,
    google_cloud_run_v2_job.pipeline_jobs,
  ]
}

# ── Health webhook receiver (AI pipeline — public ingress) ───────────────────────────────────
# The platform's only public-ingress pipeline surface: authenticates the Subscriber secret,
# ACKs 204, forwards the raw notification to the realtime topic. Runs as a dedicated service
# account holding exactly two grants — read its own secret, publish to the topic — and the
# container carries no database, AI or business configuration at all.

variable "enable_webhook_receiver" {
  description = "Create the Google Health webhook receiver service. Requires enable_pubsub"
  type        = bool
  default     = false
}

variable "webhook_receiver_name" {
  description = "Name of the webhook receiver Cloud Run service"
  type        = string
  default     = ""
}

variable "webhook_receiver_container_image" {
  description = "Bootstrap image seeding the receiver's initial create; CI/CD owns it thereafter"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "webhook_receiver_env_vars" {
  description = "Environment variables for the webhook receiver"
  type        = map(string)
  default     = {}
}

resource "google_service_account" "webhook_receiver" {
  count        = var.enable_webhook_receiver ? 1 : 0
  account_id   = "webhook-receiver"
  display_name = "CardiTrack health webhook receiver runtime"
}

resource "google_secret_manager_secret_iam_member" "webhook_secret_accessor" {
  count     = var.enable_webhook_receiver ? 1 : 0
  secret_id = google_secret_manager_secret.webhook_secret[0].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.webhook_receiver[0].email}"
}

resource "google_pubsub_topic_iam_member" "webhook_receiver_publisher" {
  count  = var.enable_webhook_receiver ? 1 : 0
  topic  = google_pubsub_topic.realtime[0].name
  role   = "roles/pubsub.publisher"
  member = "serviceAccount:${google_service_account.webhook_receiver[0].email}"

  lifecycle {
    precondition {
      condition     = var.enable_pubsub
      error_message = "enable_webhook_receiver requires enable_pubsub — the receiver publishes to the realtime topic."
    }
  }
}

resource "google_cloud_run_v2_service" "webhook_receiver" {
  count    = var.enable_webhook_receiver ? 1 : 0
  name     = var.webhook_receiver_name
  location = var.cloud_run_location
  ingress  = local.webhook_has_domain ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    service_account = google_service_account.webhook_receiver[0].email

    scaling {
      min_instance_count = 0
      max_instance_count = var.cloud_run_max_instances
    }

    containers {
      image = var.webhook_receiver_container_image

      dynamic "env" {
        for_each = var.webhook_receiver_env_vars
        iterator = item
        content {
          name  = item.key
          value = item.value
        }
      }

      env {
        name = "Webhook__Secret"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.webhook_secret[0].secret_id
            version = "latest"
          }
        }
      }

      resources {
        limits = {
          cpu    = "1"
          memory = "512Mi"
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.webhook_secret,
  ]
}

# Public by design: Google's webhook delivery carries the Subscriber secret, not an OIDC
# identity, so the service itself is the authentication boundary.
resource "google_cloud_run_v2_service_iam_member" "webhook_receiver_public" {
  count    = var.enable_webhook_receiver ? 1 : 0
  name     = google_cloud_run_v2_service.webhook_receiver[0].name
  location = google_cloud_run_v2_service.webhook_receiver[0].location
  role     = "roles/run.invoker"
  member   = "allUsers"
}

# ── Pipeline aggregator job (AI pipeline — webhook aggregation) ──────────────────────────────
# Same image as the digest job, selected via container args: drains the realtime subscription
# every 5 minutes and runs a targeted sync for each notified wearer. Exists only when both the
# pipeline and Pub/Sub are enabled — without the topic there is nothing to drain.

variable "pipeline_aggregator_env_vars" {
  description = "Environment variables for the aggregator job"
  type        = map(string)
  default     = {}
}

variable "pipeline_aggregator_secret_env_vars" {
  description = "Secret Manager-backed env vars for the aggregator job (key=env var name, value=secret ID)"
  type        = map(string)
  default     = {}
}

variable "pipeline_aggregator_schedule" {
  description = "Cloud Scheduler cron for the aggregator job — every 5 minutes per the pipeline design"
  type        = string
  default     = "*/5 * * * *"
}

locals {
  pipeline_aggregator_enabled = var.enable_pipeline_jobs && var.enable_pubsub
}

resource "google_cloud_run_v2_job" "pipeline_aggregator" {
  count    = local.pipeline_aggregator_enabled ? 1 : 0
  name     = "${var.pipeline_jobs_name}-aggregator"
  location = var.cloud_run_location
  client   = "terraform"

  template {
    template {
      max_retries = 1
      timeout     = "1800s"

      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "PRIVATE_RANGES_ONLY"
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [google_sql_database_instance.main.connection_name]
        }
      }

      containers {
        image = var.pipeline_jobs_container_image
        args  = ["--job", "aggregate"]

        dynamic "env" {
          for_each = var.pipeline_aggregator_env_vars
          iterator = item
          content {
            name  = item.key
            value = item.value
          }
        }

        dynamic "env" {
          for_each = var.pipeline_aggregator_secret_env_vars
          iterator = item
          content {
            name = item.key
            value_source {
              secret_key_ref {
                secret  = item.value
                version = "latest"
              }
            }
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }

        resources {
          limits = {
            cpu    = "1"
            memory = "512Mi"
          }
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.db_connection_string,
    google_secret_manager_secret_version.medgemma_service_url,
    google_secret_manager_secret_version.rewrite_service_url,
    # The aggregator runs as the default compute SA; on 2026-08-20 its update raced the fresh
    # rewrite-URL grant by 18 seconds and was rejected — see the barrier in service_accounts.tf.
    time_sleep.compute_sa_secret_propagation,
  ]
}

# The jobs run as the default compute service account (like every other Cloud Run resource
# here except the webhook receiver), so the subscription grant goes to it.
resource "google_pubsub_subscription_iam_member" "pipeline_aggregator_subscriber" {
  count        = local.pipeline_aggregator_enabled ? 1 : 0
  subscription = google_pubsub_subscription.realtime[0].id
  role         = "roles/pubsub.subscriber"
  member       = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

resource "google_cloud_run_v2_job_iam_member" "pipeline_aggregator_invoker" {
  count    = local.pipeline_aggregator_enabled ? 1 : 0
  name     = google_cloud_run_v2_job.pipeline_aggregator[0].name
  location = google_cloud_run_v2_job.pipeline_aggregator[0].location
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.pipeline_scheduler[0].email}"
}

resource "google_cloud_scheduler_job" "pipeline_aggregator_5min" {
  count            = local.pipeline_aggregator_enabled ? 1 : 0
  name             = "${var.pipeline_jobs_name}-aggregator-5min"
  region           = var.cloud_run_location
  schedule         = var.pipeline_aggregator_schedule
  time_zone        = "Etc/UTC"
  attempt_deadline = "320s"

  http_target {
    http_method = "POST"
    uri         = "https://run.googleapis.com/v2/projects/${var.project_id}/locations/${var.cloud_run_location}/jobs/${var.pipeline_jobs_name}-aggregator:run"

    oauth_token {
      service_account_email = google_service_account.pipeline_scheduler[0].email
    }
  }

  depends_on = [
    google_project_service.cloudscheduler,
    google_cloud_run_v2_job.pipeline_aggregator,
  ]
}

# ── Pipeline assessor job (AI pipeline — real-time assessment) ───────────────────────────────
# Same image as the digest job, selected via container args: SSA over each member's latest
# hour of heart rate, one MedGemma assessment per moved window, severity routed to alerts,
# then a digest pass so a window just flagged as a problem rewrites the family summary on
# the same execution rather than waiting for the next */30 digest schedule. Works entirely
# off the granular store, so it needs the digest job's exact environment (database + MedGemma
# + encryption) and reuses those variables — no device credentials and no Pub/Sub. Gated on
# the pipeline alone: unlike the aggregator it consumes no topic, and it is useful with
# polling-only ingestion.

# Every 5 minutes, two minutes after the aggregator (`*/5` → this is `2-59/5`). The SSA
# pre-filter in RealtimeAssessmentService skips MedGemma unless the latest reading sits at
# least SampleJumpScore typical jitters from trend, so a tighter cadence no longer buys a
# cold start per calm member. Unmoved windows still short-circuit on ExistsAsync; ordinary
# windows are not stored, so a later tick can still consult the model if the hour jumps.
#
# Offset from the aggregator so a fresh sync tends to land before the assessment pass that
# reads it. Two minutes is enough for the aggregator's targeted sync on a typical member
# and keeps the historical :02/:32 ticks as a subset of the new schedule.
variable "pipeline_assessor_schedule" {
  description = "Cloud Scheduler cron for the assessor job — every 5 minutes, offset two minutes from the aggregator so one member's fresh sync tends to land before the next assessment pass. MedGemma is consulted only when SSA says the window jumped; a tighter cadence therefore scores often without inferring on every calm member"
  type        = string
  default     = "2-59/5 * * * *"
}

resource "google_cloud_run_v2_job" "pipeline_assessor" {
  count    = var.enable_pipeline_jobs ? 1 : 0
  name     = "${var.pipeline_jobs_name}-assessor"
  location = var.cloud_run_location
  client   = "terraform"

  template {
    template {
      max_retries = 1

      # Assessment plus the digest refresh that follows it on this job (see Program.cs): one
      # CPU-served MedGemma call per member whose window moved, then another per member whose
      # summary is now due. The timeout matches the digest job so a busy pass of both stages
      # is not killed mid-generation.
      timeout = "3600s"

      service_account = google_service_account.pipeline[0].email

      # PRIVATE_RANGES_ONLY: MedGemma authorises by IAM now, so its calls no longer need to leave
      # via the VPC to be recognised as internal — they carry an OIDC token instead. Only the Cloud
      # SQL private IP still needs the VPC. See the api service above and networking.tf.
      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "PRIVATE_RANGES_ONLY"
      }

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [google_sql_database_instance.main.connection_name]
        }
      }

      containers {
        image = var.pipeline_jobs_container_image
        args  = ["--job", "assess"]

        dynamic "env" {
          for_each = var.pipeline_jobs_env_vars
          iterator = item
          content {
            name  = item.key
            value = item.value
          }
        }

        dynamic "env" {
          for_each = var.pipeline_jobs_secret_env_vars
          iterator = item
          content {
            name = item.key
            value_source {
              secret_key_ref {
                secret  = item.value
                version = "latest"
              }
            }
          }
        }

        # Reach the API's internal enqueue endpoint (orange/red → push). Custom-domain
        # environments put the API behind the load balancer (INTERNAL_LOAD_BALANCER ingress),
        # so the *.run.app URI is not routable — use the public hostname there. Audience must
        # match the API's Pipeline__Audience pin, not the request URL.
        env {
          name  = "Api__BaseUrl"
          value = var.api_custom_domain != "" ? "https://${var.api_custom_domain}" : google_cloud_run_v2_service.api.uri
        }

        env {
          name  = "Pipeline__Audience"
          value = "${var.project_id}-internal-notifications"
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }

        resources {
          limits = {
            cpu    = "1"
            memory = "512Mi"
          }
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].template[0].containers[0].image, client, client_version]
  }
  depends_on = [
    google_project_service.run,
    google_secret_manager_secret_version.db_connection_string,
    # See the barrier's comment in service_accounts.tf.
    time_sleep.pipeline_iam_propagation,
  ]
}

resource "google_cloud_run_v2_job_iam_member" "pipeline_assessor_invoker" {
  count    = var.enable_pipeline_jobs ? 1 : 0
  name     = google_cloud_run_v2_job.pipeline_assessor[0].name
  location = google_cloud_run_v2_job.pipeline_assessor[0].location
  role     = "roles/run.invoker"
  member   = "serviceAccount:${google_service_account.pipeline_scheduler[0].email}"
}

resource "google_cloud_scheduler_job" "pipeline_assessor_5min" {
  count            = var.enable_pipeline_jobs ? 1 : 0
  name             = "${var.pipeline_jobs_name}-assessor-5min"
  region           = var.cloud_run_location
  schedule         = var.pipeline_assessor_schedule
  time_zone        = "Etc/UTC"
  attempt_deadline = "320s"

  http_target {
    http_method = "POST"
    uri         = "https://run.googleapis.com/v2/projects/${var.project_id}/locations/${var.cloud_run_location}/jobs/${var.pipeline_jobs_name}-assessor:run"

    oauth_token {
      service_account_email = google_service_account.pipeline_scheduler[0].email
    }
  }

  depends_on = [
    google_project_service.cloudscheduler,
    google_cloud_run_v2_job.pipeline_assessor,
  ]
}
