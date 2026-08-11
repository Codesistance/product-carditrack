provider "google" {
  project        = var.project_id
  region         = var.region
  default_labels = local.common_labels
}

# Firebase resources (google_firebase_project, google_firebase_apple_app,
# google_firebase_android_app in deployments/firebase.tf) are google-beta only.
provider "google-beta" {
  project        = var.project_id
  region         = var.region
  default_labels = local.common_labels
}
