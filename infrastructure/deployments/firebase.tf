# Firebase / FCM — push notification sending credentials
#
# Provisioning only: no send path exists yet (Phase 3 of notification_engine.md, R2).
# Enabling this now is deliberate — Apple/Google provisioning has weeks of lead time,
# and that lead time doesn't shrink by waiting for R2 (see notification_engine.md §16).
#
# Deliberately no service-account key file: the Cloud Run runtime service account picks
# up Application Default Credentials, so FirebaseAdmin can send without a long-lived key
# to leak or rotate. See the fcm_sender resource below for which SA that is today, and why.

variable "enable_push_notifications" {
  description = "Enable Firebase Cloud Messaging (FCM) for push notification sending. Provisioning ahead of the Phase 3 send path — see docs/technical/notification_engine.md §16"
  type        = bool
  default     = false
}

locals {
  # Same bundle ID on both platforms, per docs/apps/mobile/store_provisioning.md.
  mobile_bundle_id = "com.codesistance.carditrack.mobile"
}

# firebase.googleapis.com / fcm.googleapis.com enablement lives in apis.tf, alongside every
# other google_project_service resource in this deployment.

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
# ADC on Cloud Run supplies the credential.
#
# Deliberately broad for now: this SA is shared by every Cloud Run resource in this
# deployment that doesn't set its own template.service_account (api, web, worker, medgemma,
# and all three pipeline jobs — see the pipeline_aggregator_subscriber comment in
# cloud_run.tf), so all of them gain FCM-send rights, not just the eventual sender.
# Narrowing this to a dedicated SA now would mean guessing which service Phase 3 picks as
# the sender (docs/technical/notification_engine.md §16 — likely NotificationDispatchWorker
# per the Worker-exclusivity rule in CLAUDE.md) and rewiring Worker's existing Cloud
# SQL/Secret Manager grants onto a new identity ahead of that decision. Revisit this
# grant — move it to a dedicated SA scoped to just the sender — when Phase 3 lands.
resource "google_project_iam_member" "fcm_sender" {
  count   = var.enable_push_notifications ? 1 : 0
  project = var.project_id
  role    = "roles/firebasemessaging.admin"
  member  = "serviceAccount:${data.google_project.current.number}-compute@developer.gserviceaccount.com"

  depends_on = [google_project_service.fcm]
}
