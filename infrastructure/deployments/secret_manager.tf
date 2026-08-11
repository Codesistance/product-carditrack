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
    # encryption-key is NOT here — Terraform generates it (see "Encryption key" below).
    # Device-provider OAuth clients, one pair per provider, named
    # devices-{provider}-client-{id,secret} — Garmin, Withings and Oura follow the
    # same shape. Fitbit's pair is the Google Health API web client, created in the
    # Google console under the cloud-ops account.
    "devices-fitbit-client-id"     = "REPLACE_ME"
    "devices-fitbit-client-secret" = "REPLACE_ME"
    "apm-data"                     = "REPLACE_ME" # APM connection JSON, e.g. {"IngestUrl":"...","IngestToken":"..."} — see scripts/set-apm-secrets.sh
    # Mobile monitoring connection JSON for the engine named by apm-mobile-engine —
    # embed-safe client identifiers only (Datadog: {"ClientToken":"...","Site":"Eu1"}),
    # stamped into builds by CI. Site must be one the SDK can name; UK1 cannot be reached
    # from mobile at all — see docs/technical/apm_setup_runbook.md §5.
    "apm-mobile-data" = "REPLACE_ME"
  }

  # Secrets CI stamps into mobile builds (public identifiers, not credentials) —
  # the deploy SA needs read access to fetch them during mobile build jobs.
  mobile_build_secrets = [
    "auth0-domain",
    "auth0-audience",
    "auth0-mobile-client-id",
    "apm-mobile-data",
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

# ── Redis connection string + CA (Terraform-owned values) ────────────────────
# Both track the instance, so neither carries ignore_changes: if Memorystore is
# recreated the host, AUTH string and CA all change together, and a stale secret
# would leave the API unable to connect.

resource "google_secret_manager_secret" "redis_connection_string" {
  count     = var.enable_redis ? 1 : 0
  secret_id = "${var.secret_id_prefix}-redis-connection-string"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "redis_connection_string" {
  count  = var.enable_redis ? 1 : 0
  secret = google_secret_manager_secret.redis_connection_string[0].id
  secret_data = join(",", [
    "${google_redis_instance.main[0].host}:${google_redis_instance.main[0].port}",
    "password=${google_redis_instance.main[0].auth_string}",
    "ssl=True",
    "abortConnect=false",
  ])
}

# All of the instance's CAs, concatenated. Memorystore issues a second CA five
# years in for rotation; a bundle keeps the client working across the overlap
# rather than pinning whichever cert happens to be first.
resource "google_secret_manager_secret" "redis_ca" {
  count     = var.enable_redis ? 1 : 0
  secret_id = "${var.secret_id_prefix}-redis-ca"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "redis_ca" {
  count       = var.enable_redis ? 1 : 0
  secret      = google_secret_manager_secret.redis_ca[0].id
  secret_data = join("\n", google_redis_instance.main[0].server_ca_certs[*].cert)
}

resource "google_secret_manager_secret_iam_member" "redis_connection_string_accessor" {
  count     = var.enable_redis ? 1 : 0
  secret_id = google_secret_manager_secret.redis_connection_string[0].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

resource "google_secret_manager_secret_iam_member" "redis_ca_accessor" {
  count     = var.enable_redis ? 1 : 0
  secret_id = google_secret_manager_secret.redis_ca[0].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# ── Auth0 + device OAuth + APM (placeholder values, operator-overwritten) ─────

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

# ── Encryption key (Terraform-owned, generated) ──────────────────────────────
# Base64-encoded 256-bit AES-GCM key for field-level encryption of device OAuth
# tokens (Encryption__Key). Generated here rather than left as a REPLACE_ME
# placeholder: the placeholder is not valid base64, so an unseeded environment
# fails at runtime the moment a device endpoint is hit.
#
# ignore_changes on the version is load-bearing, not cosmetic. Rotating this key
# makes every token already encrypted under the old one permanently undecryptable
# — there is no key id in the ciphertext envelope today (see
# docs/technical/data_protection_architecture.md §7.1). Environments that already
# hold a value therefore keep it; only a fresh environment takes the generated one.

resource "random_bytes" "encryption_key" {
  length = 32 # 256 bits
}

resource "google_secret_manager_secret" "encryption_key" {
  secret_id = "${var.secret_id_prefix}-encryption-key"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "encryption_key" {
  secret      = google_secret_manager_secret.encryption_key.id
  secret_data = random_bytes.encryption_key.base64

  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret_iam_member" "encryption_key_accessor" {
  secret_id = google_secret_manager_secret.encryption_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# Adopt the secret, version and IAM binding the placeholder loop created in
# already-provisioned environments instead of destroying and recreating them.
moved {
  from = google_secret_manager_secret.app_secrets["encryption-key"]
  to   = google_secret_manager_secret.encryption_key
}

moved {
  from = google_secret_manager_secret_version.app_secrets["encryption-key"]
  to   = google_secret_manager_secret_version.encryption_key
}

moved {
  from = google_secret_manager_secret_iam_member.app_secrets_accessor["encryption-key"]
  to   = google_secret_manager_secret_iam_member.encryption_key_accessor
}

# ── Ack token key (Terraform-owned, generated) ────────────────────────────────
# Base64-encoded 256-bit HMAC-SHA256 key signing the push delivery spine's ack/
# fetch tokens (Notifications__AckTokenKey — notification_engine.md §7.2 C3/C5).
# Same generate-not-placeholder reasoning as the encryption key above: a
# REPLACE_ME value isn't valid base64, so an unseeded environment would fail the
# moment the first push send tries to issue a token.
#
# ignore_changes on the version is load-bearing for a different reason than the
# encryption key's: there's no data to become permanently unreadable (ack tokens
# are short-lived), but rotating mid-flight would fail every ack/escalation-halt
# for whatever was in flight at rotation time. Same mitigation, different cost.
#
# Only two IAM grants — API and Worker — not every service this pattern usually
# reaches. The AI pipeline never sends push directly; it POSTs to the internal
# enqueue endpoint and the API does the actual send (see PushServiceExtensions.
# AddPushServices' remarks), so it has no reason to hold this key. Resist
# "completing the pattern" by adding it everywhere Encryption__Key appears.

resource "random_bytes" "ack_token_key" {
  length = 32 # 256 bits
}

resource "google_secret_manager_secret" "ack_token_key" {
  secret_id = "${var.secret_id_prefix}-ack-token-key"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "ack_token_key" {
  secret      = google_secret_manager_secret.ack_token_key.id
  secret_data = random_bytes.ack_token_key.base64

  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret_iam_member" "ack_token_key_accessor" {
  secret_id = google_secret_manager_secret.ack_token_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"
}

# ── Mobile APM engine (Terraform-owned — the mobile monitoring switch) ────────
# Mirrors the server's Apm__Engine env var: flip apm_mobile_engine in tfvars and
# apply; CI stamps the value into mobile builds. Not in placeholder_secrets on
# purpose — its version must track Terraform, not an operator.

variable "apm_mobile_engine" {
  description = "Mobile monitoring engine stamped into app builds (must match an engine in the app's MobileApm registry, e.g. Datadog); empty disables monitoring"
  type        = string
}

resource "google_secret_manager_secret" "apm_mobile_engine" {
  secret_id = "${var.secret_id_prefix}-apm-mobile-engine"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "apm_mobile_engine" {
  secret      = google_secret_manager_secret.apm_mobile_engine.id
  secret_data = var.apm_mobile_engine
}

resource "google_secret_manager_secret_iam_member" "apm_mobile_engine_deploy_accessor" {
  secret_id = google_secret_manager_secret.apm_mobile_engine.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.deploy_service_account}"
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

# ── Webhook subscriber secret (Terraform-owned value) ────────────────────────
# The full Authorization header value Google sends with every webhook notification, registered
# in the Subscriber's endpointAuthorization.secret at provisioning time. Machine-generated and
# machine-consumed on both ends, so Terraform owns the value outright.

resource "random_password" "webhook_secret" {
  count   = var.enable_webhook_receiver ? 1 : 0
  length  = 48
  special = false # travels in an HTTP header; alphanumeric avoids any escaping ambiguity
}

resource "google_secret_manager_secret" "webhook_secret" {
  count     = var.enable_webhook_receiver ? 1 : 0
  secret_id = "${var.secret_id_prefix}-webhook-secret"
  replication {
    auto {}
  }
  labels     = var.secret_labels
  depends_on = [google_project_service.secretmanager]
}

resource "google_secret_manager_secret_version" "webhook_secret" {
  count       = var.enable_webhook_receiver ? 1 : 0
  secret      = google_secret_manager_secret.webhook_secret[0].id
  secret_data = "Bearer ${random_password.webhook_secret[0].result}"
}
