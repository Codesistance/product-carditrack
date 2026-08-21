variable "project_id" {
  description = "GCP project ID"
  type        = string
}

variable "region" {
  description = "GCP region"
  type        = string
  default     = "europe-west2"
}

variable "project_name" {
  description = "Project name used for resource naming"
  type        = string
  default     = "carditrack"
}

# ── MedGemma (shared GPU service) ────────────────────────────────────────────────────────────

# Cloud Run does not offer L4 in europe-west2, where the rest of this estate sits. europe-west1
# is the nearest region that does and is inside the EU boundary the DPIA fixes (v0.11, R-A4/M4:
# europe-west1 and west4 accepted alongside west2 on 2026-08-19). Validated rather than free
# text, for the same reason the Vertex locations are: this is a residency control, and a typo
# that lands inference outside the EU has no other symptom.
variable "medgemma_location" {
  description = "Region for the shared MedGemma GPU service — must offer Cloud Run L4 and sit in the EU"
  type        = string
  default     = "europe-west1"

  validation {
    condition     = contains(["europe-west1", "europe-west4"], var.medgemma_location)
    error_message = "medgemma_location must be europe-west1 or europe-west4 — the EU regions offering Cloud Run L4."
  }
}

# Empty means the service is not created at all, which is how this lands without a chicken-and-egg
# problem: the first apply builds the registry, CI pushes an image, and the tfvar names it. Seeded
# with Cloud Run's hello image the same way the environment stacks are, so the service exists to
# be deployed into before it has ever served a model.
variable "medgemma_image" {
  description = "Container image for the shared MedGemma service. Empty disables the service entirely"
  type        = string
  default     = ""
}

# 4 vCPU is the floor Cloud Run requires above 8 GiB of memory, not a measured need — the GPU does
# the inference. Memory still has to hold the model blobs the image pulls at start.
variable "medgemma_cpu" {
  description = "CPU allocation for the shared MedGemma service"
  type        = string
  default     = "4"
}

variable "medgemma_memory" {
  description = "Memory allocation for the shared MedGemma service"
  type        = string
  default     = "16Gi"
}

# Zero between calls. This is the saving: the CPU service it replaces could not scale to zero,
# because reloading the model took 58.6s and every caller's budget was shorter than that.
variable "medgemma_min_instances" {
  description = "Minimum instances — 0 scales to zero between calls, which is the point of the GPU move"
  type        = number
  default     = 0
}

# One. Two callers do not go faster in parallel on a single accelerator, and the client treats the
# resulting 429 as saturation and backs off. Raising this is a capacity decision with a cost
# attached, not a tuning knob.
variable "medgemma_max_instances" {
  description = "Maximum instances"
  type        = number
  default     = 1
}

# Matches the environment stacks' own medgemma_timeout_seconds — the service's request timeout is
# derived from it as +60s so the client always owns the deadline. Restated here because the two
# roots have separate state and cannot share a variable; they must be changed together.
variable "medgemma_timeout_seconds" {
  description = "Caller's per-request budget. The service timeout is this plus 60s"
  type        = number
  default     = 900
}

# Constructed service-account emails, not a lookup: the callers live in the environment stacks,
# whose state this root cannot read. An entry for an account that does not exist yet applies
# cleanly and grants nothing, so prod's go in only once its accounts are confirmed (M6).
variable "medgemma_invoker_members" {
  description = "IAM members granted roles/run.invoker on the shared MedGemma service"
  type        = list(string)
  default     = []
}
