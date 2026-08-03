# Secret Manager
# Manages Google Cloud Secret Manager secrets

# Variables
variable "db_password_secret_id" {
  description = "Secret Manager secret ID for database password"
  type        = string
}

variable "secret_id_prefix" {
  description = "Prefix for app secret IDs (e.g. carditrack-dev)"
  type        = string
}

variable "secret_labels" {
  description = "Labels for Secret Manager resources"
  type        = map(string)
  default     = {}
}

variable "deploy_service_account" {
  description = "Service account email used by CI/CD to deploy (granted read access to health token secret)"
  type        = string
}

# Locals
locals {
  # Cloud Run connects via the Cloud SQL Auth Proxy Unix socket — no public IP needed.
  # The proxy handles TLS; SSL Mode=Disable applies to the local socket only.
  db_connection_string = join(";", [
    "Host=/cloudsql/${google_sql_database_instance.main.connection_name}",
    "Database=${google_sql_database.main.name}",
    "Username=${var.db_admin_username}",
    "Password=${random_password.db_password.result}",
    "SSL Mode=Disable",
  ])

  # Secrets whose values Terraform sets with a placeholder.
  # Operators overwrite via: gcloud secrets versions add <id> --data-file=-
  # lifecycle.ignore_changes ensures subsequent applies never revert operator values.
  placeholder_secrets = {
    "auth0-domain"           = "REPLACE_ME"
    "auth0-audience"         = "REPLACE_ME"
    "auth0-client-id"        = "REPLACE_ME"
    "auth0-client-secret"    = "REPLACE_ME"
    "auth0-mobile-client-id" = "REPLACE_ME" # Auth0 Native app client for the MAUI mobile app (public, no secret)
    "encryption-key"         = "REPLACE_ME"
  }

  # Secrets CI stamps into mobile builds (public identifiers, not credentials) —
  # the deploy SA needs read access to fetch them during mobile build jobs.
  mobile_build_secrets = [
    "auth0-domain",
    "auth0-audience",
    "auth0-mobile-client-id",
  ]
}

# ── DB password ──────────────────────────────────────────────────────────────

resource "random_password" "db_password" {
  length           = 32
  special          = true
  override_special = "!#$%&*()-_=+[]{}<>:?"
}

resource "google_secret_manager_secret" "db_password" {
  secret_id = var.db_password_secret_id
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "db_password" {
  secret      = google_secret_manager_secret.db_password.id
  secret_data = random_password.db_password.result
}

# ── DB connection string (Terraform-owned value) ─────────────────────────────

resource "google_secret_manager_secret" "db_connection_string" {
  secret_id = "${var.secret_id_prefix}-db-connection-string"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "db_connection_string" {
  secret      = google_secret_manager_secret.db_connection_string.id
  secret_data = local.db_connection_string
}

# ── Auth0 + Encryption (placeholder values, operator-overwritten) ─────────────

resource "google_secret_manager_secret" "app_secrets" {
  for_each  = local.placeholder_secrets
  secret_id = "${var.secret_id_prefix}-${each.key}"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "app_secrets" {
  for_each    = local.placeholder_secrets
  secret      = google_secret_manager_secret.app_secrets[each.key].id
  secret_data = each.value

  lifecycle {
    ignore_changes = [secret_data]
  }
}

# ── Health check token (Terraform-owned, auto-rotated) ────────────────────────

resource "random_password" "health_token" {
  length  = 40
  special = false
}

resource "google_secret_manager_secret" "health_token" {
  secret_id = "${var.secret_id_prefix}-health-token"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "health_token" {
  secret      = google_secret_manager_secret.health_token.id
  secret_data = random_password.health_token.result
}

# ── IAM — Cloud Run default compute SA can read all app secrets ───────────────

resource "google_secret_manager_secret_iam_member" "db_conn_accessor" {
  secret_id = google_secret_manager_secret.db_connection_string.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

resource "google_secret_manager_secret_iam_member" "app_secrets_accessor" {
  for_each  = local.placeholder_secrets
  secret_id = google_secret_manager_secret.app_secrets[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

resource "google_secret_manager_secret_iam_member" "health_token_compute_accessor" {
  secret_id = google_secret_manager_secret.health_token.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# Deploy SA needs read access so GitHub Actions can fetch the token during smoke tests
resource "google_secret_manager_secret_iam_member" "health_token_deploy_accessor" {
  secret_id = google_secret_manager_secret.health_token.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.deploy_service_account}"
}

# Deploy SA reads Auth0 mobile config to stamp it into mobile builds (-p: properties)
resource "google_secret_manager_secret_iam_member" "mobile_build_deploy_accessor" {
  for_each  = toset(local.mobile_build_secrets)
  secret_id = google_secret_manager_secret.app_secrets[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.deploy_service_account}"
}

# ── AI secrets (placeholder values, operator/CI-overwritten) ─────────────────

resource "google_secret_manager_secret" "gemini_api_key" {
  secret_id = "${var.secret_id_prefix}-gemini-api-key"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "gemini_api_key" {
  secret      = google_secret_manager_secret.gemini_api_key.id
  secret_data = "placeholder"
  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret_iam_member" "gemini_api_key_accessor" {
  secret_id = google_secret_manager_secret.gemini_api_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# MedGemma internal service URL — CI/CD writes the value after each deployment;
# Terraform only creates the shell. API reads it at startup via Secret Manager binding.
resource "google_secret_manager_secret" "medgemma_service_url" {
  secret_id = "${var.secret_id_prefix}-medgemma-service-url"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "medgemma_service_url" {
  secret      = google_secret_manager_secret.medgemma_service_url.id
  secret_data = "placeholder"
  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret_iam_member" "medgemma_url_accessor" {
  secret_id = google_secret_manager_secret.medgemma_service_url.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# Deploy SA needs secretVersionManager to write the URL after each MedGemma deployment
resource "google_secret_manager_secret_iam_member" "deploy_sa_medgemma_url_manager" {
  secret_id = google_secret_manager_secret.medgemma_service_url.id
  role      = "roles/secretmanager.secretVersionManager"
  member    = "serviceAccount:${var.deploy_service_account}"
}
