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
