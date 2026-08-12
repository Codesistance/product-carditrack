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

variable "medgemma_max_instances" {
  description = "Maximum number of MedGemma instances (Ollama cannot safely multi-instance)"
  type        = number
  default     = 1
}

# Deliberately not cloud_run_min_instances: at 8 vCPU / 16 Gi with cpu_idle = false a warm
# MedGemma instance is the largest line item on the bill, and prod sets that shared variable
# to 1. Scaling to zero trades a cold start (image pull + model load) for paying only while
# an instance is alive.
variable "medgemma_min_instances" {
  description = "Minimum number of MedGemma instances (0 scales to zero between requests)"
  type        = number
  default     = 0
}

# Resources
resource "google_cloud_run_v2_service" "api" {
  name     = var.api_service_name
  location = var.cloud_run_location
  ingress  = local.has_any_domain ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      # ALL_TRAFFIC, not PRIVATE_RANGES_ONLY: InsightsController calls MedGemma over its
      # public *.run.app URL. That's not an RFC1918 destination, so PRIVATE_RANGES_ONLY sends
      # it out the normal internet path instead of the VPC — MedGemma's internal-only ingress
      # then rejects it as external traffic (404, before the request ever reaches Ollama).
      egress = "ALL_TRAFFIC"
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
      # because the service account identity comes from data.google_project.current (this module,
      # outputs.tf), not something root main.tf can compute. The pipeline jobs run as this same
      # default compute SA (no dedicated identity — see the pipeline_aggregator_subscriber comment
      # below), so this is also the identity a future SeverityRouter caller would authenticate as.
      env {
        name  = "Pipeline__Audience"
        value = "${var.project_id}-internal-notifications"
      }

      env {
        name  = "Pipeline__ServiceAccount"
        value = "${data.google_project.current.number}-compute@developer.gserviceaccount.com"
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
    google_secret_manager_secret_iam_member.medgemma_url_accessor,
    google_secret_manager_secret_version.redis_connection_string,
    google_secret_manager_secret_version.redis_ca,
    google_secret_manager_secret_iam_member.redis_connection_string_accessor,
    google_secret_manager_secret_iam_member.redis_ca_accessor,
  ]
}

resource "google_cloud_run_v2_service" "web" {
  name     = var.web_service_name
  location = var.cloud_run_location
  ingress  = local.has_any_domain ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL"
  client   = "terraform"

  template {
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
    google_storage_bucket_iam_member.web_dataprotection_keys,
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
  ]
}

# ── MedGemma (Ollama) ─────────────────────────────────────────────────────────
# Internal-only: API reaches it via private VPC. URL written to Secret Manager
# by CI/CD after each deployment; not exposed publicly.
resource "google_cloud_run_v2_service" "medgemma" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = var.medgemma_service_name
  location = var.cloud_run_location
  ingress  = "INGRESS_TRAFFIC_INTERNAL_ONLY"
  client   = "terraform"

  template {
    scaling {
      min_instance_count = var.medgemma_min_instances
      max_instance_count = var.medgemma_max_instances
    }

    vpc_access {
      network_interfaces {
        network    = google_compute_network.main.id
        subnetwork = google_compute_subnetwork.main.id
      }
      egress = "PRIVATE_RANGES_ONLY"
    }

    containers {
      image = var.medgemma_image

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

# Allow unauthenticated access, same as api/web above: the real boundary is
# INGRESS_TRAFFIC_INTERNAL_ONLY on the medgemma service itself, not IAM — none of its callers
# (api, pipeline_jobs, pipeline_assessor) attach an identity token, so without this every
# request that reaches the service still 403s once ingress routing lets it through.
resource "google_cloud_run_v2_service_iam_member" "medgemma_internal" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = google_cloud_run_v2_service.medgemma[0].name
  location = google_cloud_run_v2_service.medgemma[0].location
  role     = "roles/run.invoker"
  member   = "allUsers"
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

variable "pipeline_jobs_schedule" {
  description = "Cloud Scheduler cron for the digest job. Quarter-hourly: this is how quickly a member's summary catches up after new readings, and how quickly a failed pass is retried. The job skips members whose data has not moved, and its own MinimumRegenerationInterval caps how often any one member is regenerated, so this cadence does not multiply inference cost"
  type        = string
  default     = "*/15 * * * *"
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

      # ALL_TRAFFIC, not PRIVATE_RANGES_ONLY: MedGemma's internal-only ingress requires the
      # request to actually route through the VPC to be recognized as internal. Its URL is a
      # public *.run.app address, not an RFC1918 one, so PRIVATE_RANGES_ONLY would send calls
      # to it out the normal internet path instead — MedGemma then rejects them as external
      # traffic (404, before the request ever reaches Ollama).
      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "ALL_TRAFFIC"
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
  ingress  = "INGRESS_TRAFFIC_ALL"
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
# Same image as the digest job, selected via container args: every 5 minutes, SSA over each
# member's latest hour of heart rate, one MedGemma assessment per moved window, severity routed
# to alerts. Works entirely off the granular store, so it needs the digest job's exact
# environment (database + MedGemma + encryption) and reuses those variables — no device
# credentials and no Pub/Sub. Gated on the pipeline alone: unlike the aggregator it consumes
# no topic, and it is useful with polling-only ingestion.

variable "pipeline_assessor_schedule" {
  description = "Cloud Scheduler cron for the assessor job — every 5 minutes, offset from the aggregator so one member's fresh sync tends to land before the next assessment pass"
  type        = string
  default     = "2-57/5 * * * *"
}

resource "google_cloud_run_v2_job" "pipeline_assessor" {
  count    = var.enable_pipeline_jobs ? 1 : 0
  name     = "${var.pipeline_jobs_name}-assessor"
  location = var.cloud_run_location
  client   = "terraform"

  template {
    template {
      max_retries = 1

      # One CPU-served MedGemma call per member whose window moved since the last pass; the
      # timeout bounds a pathological pass without killing an ordinary busy one.
      timeout = "1800s"

      # ALL_TRAFFIC, not PRIVATE_RANGES_ONLY: MedGemma's internal-only ingress requires the
      # request to actually route through the VPC to be recognized as internal. Its URL is a
      # public *.run.app address, not an RFC1918 one, so PRIVATE_RANGES_ONLY would send calls
      # to it out the normal internet path instead — MedGemma then rejects them as external
      # traffic (404, before the request ever reaches Ollama).
      vpc_access {
        network_interfaces {
          network    = google_compute_network.main.id
          subnetwork = google_compute_subnetwork.main.id
        }
        egress = "ALL_TRAFFIC"
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
