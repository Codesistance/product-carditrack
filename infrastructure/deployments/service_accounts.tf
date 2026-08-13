# Dedicated runtime service accounts
#
# Why these exist: MedGemma's authorisation boundary moved from network position
# (INGRESS_TRAFFIC_INTERNAL_ONLY + an allUsers invoker) to IAM (roles/run.invoker on a named
# identity). An IAM boundary is only as narrow as the identity it names, and until now every
# Cloud Run workload except the webhook receiver ran as the *default compute service account* —
# so granting run.invoker to that identity would have handed MedGemma invoke rights to web,
# worker and the migrator as well, none of which call it. The grant would have been least
# privilege in form only.
#
# Two accounts, not one. A single shared "medgemma-caller" would have given the pipeline jobs
# the API's grant set — Auth0 credentials, device OAuth client secrets, the ack-token key —
# and main.tf calls the jobs' env "deliberately the narrowest env set of any host ... so it
# cannot reach anything else". Env vars bound what is injected; IAM bounds what a workload can
# fetch for itself from Secret Manager. Sharing one identity would have widened the second
# while leaving the first looking unchanged.
#
# What deliberately did NOT move: web, worker, migrator and the aggregator stay on the default
# compute SA. They lose nothing — every grant below is additive, and no existing binding is
# removed. The standing risk that the compute SA holds secretAccessor on encryption-key while
# also running the public-facing web service is unchanged by this file and is tracked
# separately; web is the identity worth splitting next, not the API.

locals {
  # No equivalent local for the pipeline identity on purpose. That resource is counted on
  # enable_pipeline_jobs, so its attributes must be read as google_service_account.pipeline[0],
  # and a local holding that index would be evaluated even in environments where the count is
  # zero (prod today). Every consumer of it is itself gated on the same variable, so the indexed
  # reference is only ever reached where the instance exists — safe inline, unsafe hoisted.
  api_sa = "serviceAccount:${google_service_account.api.email}"

  # Secrets the API reads at instance start via secret_key_ref. Cloud Run resolves those
  # references with the *runtime* service account, so a missing grant here surfaces as a
  # revision that will not start — loud, not silent.
  api_app_secrets = local.placeholder_secrets
}

# ── API runtime identity ──────────────────────────────────────────────────────
resource "google_service_account" "api" {
  account_id   = "${var.secret_id_prefix}-api"
  display_name = "CardiTrack API (${var.secret_id_prefix})"
  description  = "Runtime identity for the API Cloud Run service. Holds run.invoker on MedGemma."
}

resource "google_project_iam_member" "api_cloudsql_client" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = local.api_sa
}

# The API's immediate-attempt push send path (notification_engine.md Phase 3) issues FCM sends
# directly, so this mirrors the compute SA's existing fcm_sender grant rather than narrowing it.
# See the note on that grant in firebase.tf: narrowing to just the eventual sender is Phase 3 work.
resource "google_project_iam_member" "api_fcm_sender" {
  count   = var.enable_push_notifications ? 1 : 0
  project = var.project_id
  role    = "roles/firebasecloudmessaging.admin"
  member  = local.api_sa

  depends_on = [google_project_service.fcm]
}

resource "google_secret_manager_secret_iam_member" "api_db_conn" {
  secret_id = google_secret_manager_secret.db_connection_string.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_app_secrets" {
  for_each  = local.api_app_secrets
  secret_id = google_secret_manager_secret.app_secrets[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_encryption_key" {
  secret_id = google_secret_manager_secret.encryption_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_ack_token_key" {
  secret_id = google_secret_manager_secret.ack_token_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_health_token" {
  secret_id = google_secret_manager_secret.health_token.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_gemini_api_key" {
  secret_id = google_secret_manager_secret.gemini_api_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_medgemma_url" {
  secret_id = google_secret_manager_secret.medgemma_service_url.id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_redis_connection_string" {
  count     = var.enable_redis ? 1 : 0
  secret_id = google_secret_manager_secret.redis_connection_string[0].id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

resource "google_secret_manager_secret_iam_member" "api_redis_ca" {
  count     = var.enable_redis ? 1 : 0
  secret_id = google_secret_manager_secret.redis_ca[0].id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.api_sa
}

# ── Pipeline runtime identity (digest + assessor jobs) ────────────────────────
# Four secrets, and that is the whole set — the same narrowness the jobs' env vars already
# claim, now true at the IAM layer too. Notably absent: Auth0, the device OAuth client
# credentials, the ack-token key, and any Pub/Sub role. The aggregator needs Pub/Sub and the
# device credentials, which is exactly why it is not on this identity.
resource "google_service_account" "pipeline" {
  count        = var.enable_pipeline_jobs ? 1 : 0
  account_id   = "${var.secret_id_prefix}-pipeline"
  display_name = "CardiTrack pipeline jobs (${var.secret_id_prefix})"
  description  = "Runtime identity for the digest and assessor Cloud Run jobs. Holds run.invoker on MedGemma."
}

resource "google_project_iam_member" "pipeline_cloudsql_client" {
  count   = var.enable_pipeline_jobs ? 1 : 0
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = "serviceAccount:${google_service_account.pipeline[0].email}"
}

resource "google_secret_manager_secret_iam_member" "pipeline_db_conn" {
  count     = var.enable_pipeline_jobs ? 1 : 0
  secret_id = google_secret_manager_secret.db_connection_string.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.pipeline[0].email}"
}

resource "google_secret_manager_secret_iam_member" "pipeline_encryption_key" {
  count     = var.enable_pipeline_jobs ? 1 : 0
  secret_id = google_secret_manager_secret.encryption_key.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.pipeline[0].email}"
}

resource "google_secret_manager_secret_iam_member" "pipeline_medgemma_url" {
  count     = var.enable_pipeline_jobs ? 1 : 0
  secret_id = google_secret_manager_secret.medgemma_service_url.id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.pipeline[0].email}"
}

resource "google_secret_manager_secret_iam_member" "pipeline_apm_data" {
  count     = var.enable_pipeline_jobs ? 1 : 0
  secret_id = google_secret_manager_secret.app_secrets["apm-data"].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.pipeline[0].email}"
}

# ── IAM propagation barrier ───────────────────────────────────────────────────
# Cloud Run validates every secret_key_ref against the revision's service account at the moment
# it creates the revision, and Secret Manager IAM is eventually consistent. Ordering alone is not
# enough: the 2026-08-13 dev apply created these bindings at 10:52:32-41 and Cloud Run still
# rejected the api revision at 10:52:53 with "Permission denied on secret ... for Revision service
# account carditrack-dev-api@". Ten bindings that Terraform had already created, ~12s earlier.
#
# depends_on would have made the ordering explicit but would not have helped — Terraform waits for
# the IAM API call to return, not for the grant to be visible to Cloud Run. So the Cloud Run
# resources depend on these barriers instead of on the bindings directly.
#
# triggers keyed on the service account email: time_sleep only sleeps when it is created, and the
# wait is only needed when the identity is new. Steady-state applies do not pay it; a fresh
# environment (prod, where neither account exists yet) does.
resource "time_sleep" "api_iam_propagation" {
  create_duration = "60s"

  triggers = {
    service_account = google_service_account.api.email
  }

  depends_on = [
    google_project_iam_member.api_cloudsql_client,
    google_project_iam_member.api_fcm_sender,
    google_secret_manager_secret_iam_member.api_db_conn,
    google_secret_manager_secret_iam_member.api_app_secrets,
    google_secret_manager_secret_iam_member.api_encryption_key,
    google_secret_manager_secret_iam_member.api_ack_token_key,
    google_secret_manager_secret_iam_member.api_health_token,
    google_secret_manager_secret_iam_member.api_gemini_api_key,
    google_secret_manager_secret_iam_member.api_medgemma_url,
    google_secret_manager_secret_iam_member.api_redis_connection_string,
    google_secret_manager_secret_iam_member.api_redis_ca,
  ]
}

resource "time_sleep" "pipeline_iam_propagation" {
  count           = var.enable_pipeline_jobs ? 1 : 0
  create_duration = "60s"

  triggers = {
    service_account = google_service_account.pipeline[0].email
  }

  depends_on = [
    google_project_iam_member.pipeline_cloudsql_client,
    google_secret_manager_secret_iam_member.pipeline_db_conn,
    google_secret_manager_secret_iam_member.pipeline_encryption_key,
    google_secret_manager_secret_iam_member.pipeline_medgemma_url,
    google_secret_manager_secret_iam_member.pipeline_apm_data,
  ]
}
