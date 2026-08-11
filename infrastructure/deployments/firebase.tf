# Firebase / FCM — push notification sending credentials
#
# Provisioning only: no send path exists yet (Phase 3 of notification_engine.md, R2).
# Enabling this now is deliberate — Apple/Google provisioning has weeks of lead time,
# and that lead time doesn't shrink by waiting for R2 (see notification_engine.md §16).
#
# Deliberately no service-account key file: the Cloud Run runtime service account picks
# up Application Default Credentials, so FirebaseAdmin can send without a long-lived key
# to leak or rotate. Same identity as every other Cloud Run resource in this deployment
# (see the pipeline_aggregator_subscriber comment in cloud_run.tf) — no dedicated FCM
# sender service account, because no sending service has been chosen yet.

variable "enable_push_notifications" {
  description = "Enable Firebase Cloud Messaging (FCM) for push notification sending. Provisioning ahead of the Phase 3 send path — see docs/technical/notification_engine.md §16"
  type        = bool
  default     = false
}

locals {
  # Same bundle ID on both platforms, per docs/apps/mobile/store_provisioning.md.
  mobile_bundle_id = "com.codesistance.carditrack.mobile"
}

resource "google_project_service" "firebase" {
  count              = var.enable_push_notifications ? 1 : 0
  service            = "firebase.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "fcm" {
  count              = var.enable_push_notifications ? 1 : 0
  service            = "fcm.googleapis.com"
  disable_on_destroy = false
}

resource "google_firebase_project" "default" {
  count    = var.enable_push_notifications ? 1 : 0
  provider = google-beta
  project  = var.project_id

  depends_on = [google_project_service.firebase]
}

resource "google_firebase_apple_app" "mobile" {
  count        = var.enable_push_notifications ? 1 : 0
  provider     = google-beta
  project      = var.project_id
  bundle_id    = local.mobile_bundle_id
  display_name = "CardiTrack"

  depends_on = [google_firebase_project.default]
}

resource "google_firebase_android_app" "mobile" {
  count        = var.enable_push_notifications ? 1 : 0
  provider     = google-beta
  project      = var.project_id
  package_name = local.mobile_bundle_id
  display_name = "CardiTrack"

  depends_on = [google_firebase_project.default]
}

# Grants the default compute service account permission to send via FCM. No key file —
# ADC on Cloud Run supplies the credential. Scoped to the whole project because
# roles/firebasemessaging.admin has no narrower, per-app IAM condition today.
resource "google_project_iam_member" "fcm_sender" {
  count   = var.enable_push_notifications ? 1 : 0
  project = var.project_id
  role    = "roles/firebasemessaging.admin"
  member  = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"

  depends_on = [google_project_service.fcm]
}
