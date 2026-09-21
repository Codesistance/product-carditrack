# Model weights bucket
#
# Our own copy of the model weights the MedGemma image is built from. The image used to
# `ollama pull` a Hugging Face tag at build time; a patched Ollama refuses that tag's cross-host
# blob redirect (CVE-2026-85180), and the tag was a mutable third-party pointer regardless. The
# vendoring workflow fetches the pinned files once and puts them here; the image build reads
# them from here and nowhere else.
#
# Not the builds bucket, deliberately: that one deletes objects after ten days, which is right
# for a mobile build and would silently take the weights with it. Versioned and never
# lifecycle-deleted, because "the bytes we built from on <date>" is an audit answer this bucket
# has to be able to give.
#
# Objects are written under a content-addressed prefix (<model>/<sha256>/<file>), so a
# re-upload of different bytes cannot overwrite the ones a build already used.

resource "google_storage_bucket" "common_model_weights" {
  name          = "${var.project_name}-common-model-weights"
  location      = "EU"
  storage_class = "STANDARD"
  force_destroy = false

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = true
  }
}

# The same deploy identity that vendors the weights builds the image from them, so it needs
# both halves. Creator rather than admin: it can add objects, not delete or overwrite them.
resource "google_storage_bucket_iam_member" "common_model_weights_ci_writer" {
  bucket = google_storage_bucket.common_model_weights.name
  role   = "roles/storage.objectCreator"
  member = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}

resource "google_storage_bucket_iam_member" "common_model_weights_ci_reader" {
  bucket = google_storage_bucket.common_model_weights.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
}
