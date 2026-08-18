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

# ── Web runtime identity ──────────────────────────────────────────────────────
# The security review behind #238 rated this the highest-value split remaining, above the API's.
# The default compute SA holds secretAccessor on encryption-key — the AES-GCM key protecting device
# OAuth tokens — plus ack-token-key, auth0-client-secret, db-connection-string and
# firebasecloudmessaging.admin. It also ran the *public-facing* web service. An SSRF or RCE in web
# reached the metadata server and minted tokens that read the field-level encryption key: the
# shortest path from an internet-exposed surface to wearer device credentials on this estate.
#
# Web turns out to need almost nothing, which is what makes this worth doing rather than merely
# worth wanting. Its whole grant set is three entries — one secret, one bucket, one project role —
# against a compute SA carrying roughly a dozen. It reads no database secret at all
# (web_secret_env_vars is apm-data alone, main.tf).
#
# It also narrows the DP key ring. That ring is deliberately unencrypted on GCS, accepted because it
# protects antiforgery tokens only; until now every compute-SA workload could read it, and after
# this only web can.
#
# Additive, like the api/pipeline split before it: no existing compute SA binding is removed here.
# The compute SA's grant on the DP bucket becomes dead once web is verified on this identity — see
# the note on it in cloud_storage.tf — and removing it is a separate, verifiable apply rather than
# something bundled into the change that moves the identity.
resource "google_service_account" "web" {
  account_id   = "${var.secret_id_prefix}-web"
  display_name = "CardiTrack Web (${var.secret_id_prefix})"
  description  = "Runtime identity for the Web Cloud Run service. Deliberately holds no database, Auth0 or encryption-key access."
}

resource "google_project_iam_member" "web_cloudsql_client" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = local.web_sa
}

# Its only secret env var.
resource "google_secret_manager_secret_iam_member" "web_apm_data" {
  secret_id = google_secret_manager_secret.app_secrets["apm-data"].id
  role      = "roles/secretmanager.secretAccessor"
  member    = local.web_sa
}

# The Data Protection key ring, mounted as a GCS volume. Cloud Run resolves volume access with the
# runtime service account, so this is what keeps antiforgery working once web stops being compute.
resource "google_storage_bucket_iam_member" "web_dpkeys" {
  bucket = google_storage_bucket.dataprotection_keys.name
  role   = "roles/storage.objectAdmin"
  member = local.web_sa
}

# Same barrier as the api and pipeline identities, for the same reason: Cloud Run validates the
# apm-data secret_key_ref against this account when it creates the revision, and Secret Manager IAM
# is eventually consistent. Without it this apply fails exactly as the 2026-08-13 one did.
resource "time_sleep" "web_iam_propagation" {
  create_duration = "60s"

  triggers = {
    service_account = google_service_account.web.email
    grants = sha256(jsonencode(sort([
      google_project_iam_member.web_cloudsql_client.id,
      google_secret_manager_secret_iam_member.web_apm_data.id,
      google_storage_bucket_iam_member.web_dpkeys.id,
    ])))
  }

  depends_on = [
    google_project_iam_member.web_cloudsql_client,
    google_secret_manager_secret_iam_member.web_apm_data,
    google_storage_bucket_iam_member.web_dpkeys,
  ]
}

locals {
  # No equivalent local for the pipeline identity on purpose. That resource is counted on
  # enable_pipeline_jobs, so its attributes must be read as google_service_account.pipeline[0],
  # and a local holding that index would be evaluated even in environments where the count is
  # zero (prod today). Every consumer of it is itself gated on the same variable, so the indexed
  # reference is only ever reached where the instance exists — safe inline, unsafe hoisted.
  api_sa = "serviceAccount:${google_service_account.api.email}"
  web_sa = "serviceAccount:${google_service_account.web.email}"

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

# Dev-only, and the API is the only reader — the endpoint this key authorizes exists nowhere
# else (notification_engine.md §13). Counted off in prod alongside the secret itself.
resource "google_secret_manager_secret_iam_member" "api_dev_push_token_key" {
  count     = var.enable_dev_push_token ? 1 : 0
  secret_id = google_secret_manager_secret.dev_push_token_key[0].id
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
# triggers covers the grant set, not just the identity. Keying on the service account email alone
# would have been too narrow: time_sleep only sleeps when it is created, so adding a secret grant to
# an *existing* account and updating the revision in the same apply would sail past the barrier and
# hit exactly the failure this exists to prevent. Hashing the binding ids fixes that — an id encodes
# secret + role + member, so any change to the set changes the hash and re-arms the wait.
#
# Steady-state applies still pay nothing: unchanged bindings hash the same. Only a fresh environment
# (prod, where neither account exists yet) or a genuine grant change waits.
resource "time_sleep" "api_iam_propagation" {
  create_duration = "60s"

  triggers = {
    service_account = google_service_account.api.email
    grants = sha256(jsonencode(sort(concat(
      [
        google_project_iam_member.api_cloudsql_client.id,
        google_secret_manager_secret_iam_member.api_db_conn.id,
        google_secret_manager_secret_iam_member.api_encryption_key.id,
        google_secret_manager_secret_iam_member.api_ack_token_key.id,
        google_secret_manager_secret_iam_member.api_health_token.id,
        google_secret_manager_secret_iam_member.api_gemini_api_key.id,
        google_secret_manager_secret_iam_member.api_medgemma_url.id,
        # Not secret_key_refs, but still worth the barrier: ordering the revision after
        # these means the first boot in a fresh environment can already sign photo URLs
        # and reach the bucket, instead of 403ing until IAM catches up.
        google_storage_bucket_iam_member.api_member_photos.id,
        google_service_account_iam_member.api_self_token_creator.id,
      ],
      values(google_secret_manager_secret_iam_member.api_app_secrets)[*].id,
      google_project_iam_member.api_fcm_sender[*].id,
      google_secret_manager_secret_iam_member.api_redis_connection_string[*].id,
      google_secret_manager_secret_iam_member.api_redis_ca[*].id,
      google_secret_manager_secret_iam_member.api_dev_push_token_key[*].id,
    ))))
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
    google_secret_manager_secret_iam_member.api_dev_push_token_key,
    google_storage_bucket_iam_member.api_member_photos,
    google_service_account_iam_member.api_self_token_creator,
  ]
}

resource "time_sleep" "pipeline_iam_propagation" {
  count           = var.enable_pipeline_jobs ? 1 : 0
  create_duration = "60s"

  # Same grant-set hashing as the api barrier above, and for the same reason.
  triggers = {
    service_account = google_service_account.pipeline[0].email
    grants = sha256(jsonencode(sort([
      google_project_iam_member.pipeline_cloudsql_client[0].id,
      google_secret_manager_secret_iam_member.pipeline_db_conn[0].id,
      google_secret_manager_secret_iam_member.pipeline_encryption_key[0].id,
      google_secret_manager_secret_iam_member.pipeline_medgemma_url[0].id,
      google_secret_manager_secret_iam_member.pipeline_apm_data[0].id,
    ])))
  }

  depends_on = [
    google_project_iam_member.pipeline_cloudsql_client,
    google_secret_manager_secret_iam_member.pipeline_db_conn,
    google_secret_manager_secret_iam_member.pipeline_encryption_key,
    google_secret_manager_secret_iam_member.pipeline_medgemma_url,
    google_secret_manager_secret_iam_member.pipeline_apm_data,
  ]
}
