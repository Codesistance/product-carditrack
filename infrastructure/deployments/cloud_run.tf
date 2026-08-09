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
