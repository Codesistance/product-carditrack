# Secret Manager — Shared / Common Secrets
# Values are set by operators via:
#   echo -n "your_value" | gcloud secrets versions add carditrack-common-<name> --data-file=-
#
# Store distribution secrets are consumed by .github/workflows/deploy-apps-dev.yml.
# Binary payloads (.p12, provisioning profile, keystore) are stored base64-encoded.


resource "google_project_service" "common_secretmanager" {
  service            = "secretmanager.googleapis.com"
  disable_on_destroy = false
}

locals {
  # Secret id becomes "${var.project_name}-common-<suffix>"
  store_distribution_secrets = toset([
    "apple-distribution-cert-p12",      # Apple distribution certificate (.p12, base64)
    "apple-cert-password",              # Password for the distribution certificate
    "appstore-provisioning-profile",    # App Store provisioning profile (base64)
    "appstore-connect-issuer-id",       # App Store Connect API issuer ID
    "appstore-connect-api-key-id",      # App Store Connect API key ID
    "appstore-connect-api-private-key", # App Store Connect API private key (.p8 contents)
    "android-keystore",                 # Android upload keystore (.jks, base64, key alias: carditrack)
    "android-keystore-password",        # Password for the upload keystore and key
    "play-service-account-key",         # Google Play service account key (JSON)
  ])

  # Operator-only secrets: not read by any deploy workflow, so carditrack-deploy
  # gets no accessor grant on these. Loaded and read manually by an operator
  # (see docs/apps/mobile/store_provisioning.md).
  operator_only_secrets = toset([
    "apns-auth-key-p8", # APNs auth key for push notifications (.p8 contents, PEM text)
    "apns-key-id",      # APNs auth key ID
    "apple-team-id",    # Apple Developer Team ID
  ])

  # Read by .github/workflows/post-digest.yml, which posts the digest routine's
  # committed digests/*.json to Slack. The routine itself never sees this — it
  # ingests untrusted web content, so it holds no Slack credential.
  digest_secrets = toset([
    "slack-bot-token", # Slack bot token (xoxb-...) with chat:write
  ])
}

resource "google_secret_manager_secret" "store_distribution" {
  for_each  = local.store_distribution_secrets
  secret_id = "${var.project_name}-common-${each.key}"

  replication {
    auto {}
  }

  depends_on = [google_project_service.common_secretmanager]
}

resource "google_secret_manager_secret_version" "store_distribution" {
  for_each    = local.store_distribution_secrets
  secret      = google_secret_manager_secret.store_distribution[each.key].id
  secret_data = "REPLACE_ME"

  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret_iam_member" "store_distribution_accessor" {
  for_each  = local.store_distribution_secrets
  secret_id = google_secret_manager_secret.store_distribution[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}

resource "google_secret_manager_secret" "operator_only" {
  for_each  = local.operator_only_secrets
  secret_id = "${var.project_name}-common-${each.key}"

  replication {
    auto {}
  }

  depends_on = [google_project_service.common_secretmanager]
}

resource "google_secret_manager_secret_version" "operator_only" {
  for_each    = local.operator_only_secrets
  secret      = google_secret_manager_secret.operator_only[each.key].id
  secret_data = "REPLACE_ME"

  lifecycle {
    ignore_changes = [secret_data]
  }
}

resource "google_secret_manager_secret" "digest" {
  for_each  = local.digest_secrets
  secret_id = "${var.project_name}-common-${each.key}"

  replication {
    auto {}
  }

  depends_on = [google_project_service.common_secretmanager]
}

resource "google_secret_manager_secret_version" "digest" {
  for_each    = local.digest_secrets
  secret      = google_secret_manager_secret.digest[each.key].id
  secret_data = "REPLACE_ME"

  lifecycle {
    ignore_changes = [secret_data]
  }
}

# Granted per secret rather than at project level. Note this does not by itself
# isolate the digest workflow: carditrack-deploy also holds project-level
# roles/secretmanager.admin from scripts/setup-gcp-auth.sh, so it can read every
# secret regardless. Capping the blast radius at the Slack token needs a
# dedicated identity for this workflow — see "Hardening still required" in
# SETUP.md. This binding is the shape that identity would use.
resource "google_secret_manager_secret_iam_member" "digest_accessor" {
  for_each  = local.digest_secrets
  secret_id = google_secret_manager_secret.digest[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}
