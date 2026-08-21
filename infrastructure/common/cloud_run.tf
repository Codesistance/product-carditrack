# ── MedGemma, on a GPU, shared by every environment ──────────────────────────────────────────
# One L4-backed Cloud Run service that dev and prod both call, replacing the per-environment CPU
# instance. The serving ADR (docs/technical/medgemma_serving_architecture.md, Option B, approved
# 2026-08-19) has the full argument; the parts that shaped this file:
#
#   - The CPU instance bills 24h/day at 4 vCPU / 16 GiB for a ~42% duty cycle, because cpu_idle
#     is false and min_instances is 1. It cannot be scaled to zero: a cold start reloads the
#     model, measured at 58.6s, which outlasts every caller's budget.
#   - On an L4 that load is seconds rather than a minute, which is what makes min = 0 viable —
#     and scale-to-zero is where the saving actually comes from, not the hardware being cheaper
#     per second. It is not: an L4 instance costs more per second and runs for far less of the day.
#   - Prompt evaluation is the real cost on CPU. Measured in dev: ~25 tokens/sec, so a
#     1,184-token clinical prompt spent 47.6s before its first output token. That number is the
#     one this migration is aimed at.
#
# A service rather than a literal Job, deviating from "batched job" as the ADR words it: the
# consumers are per-environment .NET jobs with their own databases, and a service keeps
# MedGemmaClient, the OIDC handler and the URL-secret plumbing exactly as they are. The honest
# cost of that choice is Cloud Run's idle scale-in tail — an instance lingers after the last
# request — so real spend may land nearer $70-120/mo than the ADR's $40. Still several times
# below the ~$300 it replaces. Month one's billing decides whether a per-environment multi-
# container Job is worth revisiting.

resource "google_project_service" "common_run" {
  service            = "run.googleapis.com"
  disable_on_destroy = false
}

# Region is its own variable, not var.region. Artifact Registry and the builds bucket sit in
# europe-west2 with the rest of the estate; Cloud Run does not offer L4 there. europe-west1 is
# the nearest region that does, and it is inside the EU processing boundary the DPIA fixes
# (v0.11, R-A4/M4 — europe-west1 and west4 were accepted alongside west2 on 2026-08-19), so this
# is a region change and not a residency change.
resource "google_cloud_run_v2_service" "medgemma" {
  count    = var.medgemma_image != "" ? 1 : 0
  name     = "${var.project_name}-common-medgemma"
  location = var.medgemma_location

  # ALL, where the per-environment service was INTERNAL_ONLY — a deliberate loss, not an
  # oversight. That service satisfied internal ingress because the API's vpc_access carried its
  # calls onto the internal path; the callers are in europe-west2 with PRIVATE_RANGES_ONLY egress
  # and this service is in europe-west1 with no VPC attachment, so their calls arrive over the
  # public endpoint and internal ingress would refuse them.
  #
  # So this endpoint is reachable from the internet and authorisation is IAM alone: the two named
  # invokers below, no allUsers, and the public-exposure alert in alerting.tf. Unauthenticated
  # requests arrive and are refused — ten inside twenty seconds on the day it went live. Nothing
  # about who may invoke it changed; the second, network-position layer in front of the same IAM
  # check did. Recorded in dpia.md §4.3.
  ingress = "INGRESS_TRAFFIC_ALL"
  client  = "terraform"

  template {
    # Zero is where the saving comes from, and it is not free. Measured on this service: a cold
    # start loads the model in ~54s — against the CPU service's 58.6s, because loading ~3GB of
    # weights off disk is IO, not arithmetic, and the accelerator barely helps. The migration plan
    # assumed otherwise; it was wrong.
    #
    # Batch passes do not care. The first chat question after an idle spell does, and avoiding
    # exactly that is why the CPU service kept min = 1. This trades interactive latency after
    # idle for cost, knowingly — see medgemma_serving_architecture.md §9.1a, and revisit alongside
    # the first month's billing.
    scaling {
      min_instance_count = var.medgemma_min_instances
      max_instance_count = var.medgemma_max_instances
    }

    # One at a time, for the reason the CPU service documents at length: Ollama admits every
    # request it is offered and splits the accelerator between them, so a second concurrent
    # caller does not get served sooner — it makes the first one slower. A 429 refused in 0ms is
    # the honest answer, and MedGemmaClient already treats it as saturation and backs off.
    max_instance_request_concurrency = 1

    # One minute past the caller's own deadline, so the client always loses the race and owns the
    # retry decision. Derived rather than restated, so the two cannot drift.
    timeout = "${var.medgemma_timeout_seconds + 60}s"

    # No vpc_access. The env-stack service carries one, but its rationale lapsed: the subnets are
    # regional and this service is in another region, and nothing here needs to reach a private
    # range. Callers reach it over Cloud Run's own ingress with an OIDC token, which is the
    # control that matters and is unchanged.

    # The accelerator. Zonal redundancy off is not a corner cut: on it, Cloud Run reserves
    # capacity in a second zone and bills for it, which for a single-instance batch-ish service
    # doubles the cost of the thing being optimised. A zone outage here degrades to the same
    # 503-and-retry the client already handles.
    gpu_zonal_redundancy_disabled = true

    node_selector {
      accelerator = "nvidia-l4"
    }

    containers {
      image = var.medgemma_image

      # Resident for the instance's life. On the CPU service this avoided a 58.6s reload between
      # calls; here the reload is far cheaper, but the instance is short-lived and the model
      # would otherwise be evicted after five idle minutes inside a life that is often shorter.
      env {
        name  = "OLLAMA_KEEP_ALIVE"
        value = "-1"
      }

      # llama.cpp's host-side prompt cache, disabled — see the CPU service's own note for the
      # measurements. Gemma 3's sliding-window attention means the cache is written and never
      # read, costing ~336ms and ~508 MiB per request. The GPU does not change that: SWA is a
      # property of the model, not the hardware.
      env {
        name  = "LLAMA_ARG_CACHE_RAM"
        value = "0"
      }

      resources {
        limits = {
          cpu              = var.medgemma_cpu
          memory           = var.medgemma_memory
          "nvidia.com/gpu" = "1"
        }

        # Required with a GPU attached, and correct regardless: the model occupies the
        # accelerator whether or not a request is in flight, so there is no idle state to bill
        # differently. min_instance_count = 0 is what stops that mattering.
        cpu_idle          = false
        startup_cpu_boost = true
      }

      ports {
        container_port = 8080
      }

      # Wider than the CPU service's 30 + 12×10. The image carries multi-GB model blobs and a
      # cold start pulls them before Ollama answers anything, and this service cold-starts on
      # purpose rather than by accident. 150s of envelope is a starting point taken from the ADR
      # rather than a measurement — M0's benchmark replaces it with one.
      startup_probe {
        http_get {
          path = "/"
        }
        initial_delay_seconds = 30
        period_seconds        = 10
        failure_threshold     = 15
      }
    }
  }

  labels = local.labels

  lifecycle {
    # Same contract as every other service here: CI deploys images, Terraform owns the shape.
    # Without this, an apply would roll the service back to whatever image the tfvar last named.
    ignore_changes = [template[0].containers[0].image, client, client_version]
  }

  depends_on = [google_project_service.common_run]
}

# ── Who may call it ──────────────────────────────────────────────────────────────────────────
# Cross-stack IAM without cross-stack state. The callers live in the environment stacks, which
# have their own state files this root cannot read, so the members are named in common.tfvars as
# constructed service-account emails rather than resolved from a data source or a remote state.
#
# That makes them a promise rather than a lookup: an email listed here for an account that does
# not exist yet still applies cleanly and grants nothing. Prod's entries go in only once its
# service accounts are confirmed to exist (M6), so the list stays a description of reality.
resource "google_cloud_run_v2_service_iam_member" "medgemma_invokers" {
  for_each = var.medgemma_image != "" ? toset(var.medgemma_invoker_members) : toset([])

  name     = google_cloud_run_v2_service.medgemma[0].name
  location = google_cloud_run_v2_service.medgemma[0].location
  role     = "roles/run.invoker"
  member   = each.value
}
