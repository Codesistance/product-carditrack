# Secret Manager — Shared / Common Secrets
# Values are set by operators via:
#   echo -n "your_value" | gcloud secrets versions add carditrack-common-<name> --data-file=-
#
# Store distribution secrets are consumed by .github/workflows/deploy-apps-dev.yml.
# slack-bot-token and slack-channel-id are consumed by .github/workflows/post-digest.yml —
# grouped into this same set to share its carditrack-deploy accessor grant rather than a
# second identity, and slack-channel-id lives here as a secret rather than a GitHub Actions
# repo variable so every post-digest.yml input loads the same way. See SETUP.md.
# Binary payloads (.p12, provisioning profile, keystore) are stored base64-encoded.


resource "google_project_service" "common_secretmanager" {
  service            = "secretmanager.googleapis.com"
  disable_on_destroy = false
}

# slack-bot-token used to be its own resource block (google_secret_manager_secret.digest
# et al.) before it was folded into store_distribution_secrets above. Without these, a
# state that already has the old addresses would plan a destroy-then-recreate of the
# secret, its version, and its accessor binding — losing any operator-loaded token and
# risking a same-secret_id conflict before the old object is gone. A no-op if the old
# addresses were never in state (e.g. this secret's create never actually applied — see
# the 404 that prompted this change, SETUP.md "Accepted tradeoff").
moved {
  from = google_secret_manager_secret.digest["slack-bot-token"]
  to   = google_secret_manager_secret.store_distribution["slack-bot-token"]
}

moved {
  from = google_secret_manager_secret_version.digest["slack-bot-token"]
  to   = google_secret_manager_secret_version.store_distribution["slack-bot-token"]
}

moved {
  from = google_secret_manager_secret_iam_member.digest_accessor["slack-bot-token"]
  to   = google_secret_manager_secret_iam_member.store_distribution_accessor["slack-bot-token"]
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
    "slack-bot-token",                  # Slack bot token (xoxb-..., chat:write scope)
    "slack-channel-id",                 # Digest channel ID (C0XXXXXXX, not "#name")
  ])

  # Operator-only secrets: not read by any deploy workflow, so carditrack-deploy
  # gets no accessor grant on these. Loaded and read manually by an operator
  # (see docs/apps/mobile/store_provisioning.md).
  operator_only_secrets = toset([
    "apns-auth-key-p8", # APNs auth key for push notifications (.p8 contents, PEM text)
    "apns-key-id",      # APNs auth key ID
    "apple-team-id",    # Apple Developer Team ID
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
