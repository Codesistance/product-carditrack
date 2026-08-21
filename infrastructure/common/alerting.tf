# ── MedGemma public-exposure alerting ────────────────────────────────────────────────────────
#
# Ported from the environment stacks, because the thing it watches moved. PR #238 put MedGemma's
# authorisation on IAM rather than network position, and said the control making an accidental
# allUsers grant impossible rather than merely discouraged was the
# constraints/iam.allowedPolicyMemberDomains org policy.
#
# That control does not exist and cannot be created: this project has no organization, and Domain
# Restricted Sharing allow-lists Cloud Identity customer IDs, so with no organization there is no
# directory to name. The constraint is meaningless rather than merely unset, and VPC Service
# Controls and the org tiers of Security Command Center are unavailable for the same reason. That
# is a standing accepted risk, not a pending task — nothing at the platform level can stop someone
# adding allUsers back to the medical model, so the residual control is noticing quickly.
#
# Moving the service without moving this would have quietly dropped that detection at the moment
# the exposure got *larger*: one instance now serves every environment, so a public grant here is
# a public grant on the model behind all of them.
#
# Two limits worth stating rather than implying coverage this does not have:
#   - It watches the service's own IAM policy. Granting roles/run.invoker to allUsers at the
#     project level would achieve the same exposure and would not match this filter.
#   - It is detective, not preventive. The grant takes effect immediately; the alert follows.

resource "google_project_service" "common_monitoring" {
  service            = "monitoring.googleapis.com"
  disable_on_destroy = false
}

variable "alert_notification_emails" {
  description = "Addresses notified when the shared MedGemma service's IAM policy grants public access"
  type        = list(string)
  default     = []
}

locals {
  # Gated on both: an alert with no channel is a policy that fires into nothing, which is worse
  # than no policy because it reads as covered. The service gate matches the resource's own, so an
  # apply with medgemma_image empty does not try to watch a service that is not there.
  medgemma_iam_alerting = var.medgemma_image != "" && length(var.alert_notification_emails) > 0

  # condition_matched_log rather than a metric threshold, for the reason the environment stack
  # recorded: a metric threshold must restrict on resource.type, and a Cloud Run SetIamPolicy
  # audit entry can land on cloud_run_revision or on audited_resource. Guessing wrong produces a
  # policy that is valid, silent, and indistinguishable from "no incident" — the exact failure
  # this control exists to avoid.
  medgemma_public_iam_filter = <<-EOT
    logName="projects/${var.project_id}/logs/cloudaudit.googleapis.com%2Factivity"
    AND protoPayload.serviceName="run.googleapis.com"
    AND protoPayload.methodName=~"SetIamPolicy"
    AND protoPayload.resourceName=~"${var.project_name}-common-medgemma$"
    AND (
      protoPayload.request.policy.bindings.members:"allUsers"
      OR protoPayload.request.policy.bindings.members:"allAuthenticatedUsers"
      OR protoPayload.response.bindings.members:"allUsers"
      OR protoPayload.response.bindings.members:"allAuthenticatedUsers"
    )
  EOT
}

resource "google_monitoring_notification_channel" "medgemma_email" {
  for_each     = local.medgemma_iam_alerting ? toset(var.alert_notification_emails) : toset([])
  display_name = "CardiTrack common alerts — ${each.value}"
  type         = "email"
  labels = {
    email_address = each.value
  }
  depends_on = [google_project_service.common_monitoring]
}

resource "google_monitoring_alert_policy" "medgemma_public_iam" {
  count        = local.medgemma_iam_alerting ? 1 : 0
  display_name = "${var.project_name}-common-medgemma public IAM grant"
  combiner     = "OR"

  conditions {
    display_name = "MedGemma IAM policy granted public access"
    condition_matched_log {
      filter = local.medgemma_public_iam_filter
    }
  }

  # Mandatory for log-match conditions — the API rejects the policy without it. Deliberately
  # short: this fires per matching entry, and a grant being applied repeatedly is itself worth
  # seeing.
  alert_strategy {
    notification_rate_limit {
      period = "300s"
    }
  }

  notification_channels = [for c in google_monitoring_notification_channel.medgemma_email : c.id]

  documentation {
    content   = <<-EOT
      **The shared MedGemma service's IAM policy now grants `allUsers` or `allAuthenticatedUsers`.**

      This is the medical model, and one instance serves every environment — so this is public
      access to the model behind dev and prod both. Its prompts carry age, sex, free-text
      MedicalNotes and questionnaire answers (DPIA row A5).

      Revoke the binding, then find out who added it:

          gcloud run services remove-iam-policy-binding ${var.project_name}-common-medgemma \
            --region=${var.medgemma_location} --member=allUsers --role=roles/run.invoker

      The audit entry that triggered this names the principal.
    EOT
    mime_type = "text/markdown"
  }

  depends_on = [google_project_service.common_monitoring]
}
