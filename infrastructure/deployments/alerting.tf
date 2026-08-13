# Cloud Run OOM alerting
#
# 2026-08-11: carditrack-dev-worker was OOM-killed and sat dead for ~3 hours, silently, until
# the next routine redeploy replaced the instance. It was only caught by manually reading logs
# (see issue #171). This gives that class of failure an actual alert instead of relying on
# someone happening to look.

# Variables

variable "enable_oom_alerting" {
  description = "Create the Cloud Run OOM log-based metric, alert policy, and email notification channels"
  type        = bool
  default     = true

  validation {
    # An alert policy with nobody attached defeats the point of this file and fails silently —
    # no error, just an alert that fires into the void. Catch it at plan time instead.
    condition = (
      !var.enable_oom_alerting ||
      length(var.alert_notification_emails) > 0 ||
      (var.enable_slack_alerts && var.alert_slack_channel_id != "")
    )
    error_message = "enable_oom_alerting requires at least one notification channel: set alert_notification_emails, or enable_slack_alerts with a non-empty alert_slack_channel_id."
  }
}

variable "oom_alert_name" {
  description = "Name for the OOM log-based metric and alert policy, environment-qualified (dev and prod share one GCP project, so this must be unique per environment)"
  type        = string
}

variable "oom_alert_service_prefix" {
  description = "Cloud Run service name prefix (e.g. carditrack-dev-) scoping the OOM log filter to this environment's own services, so a dev OOM doesn't also fire prod's alert and vice versa"
  type        = string
}

variable "alert_notification_emails" {
  description = "Email addresses notified when a Cloud Run container in this environment is OOM-killed. One notification channel is created per address"
  type        = list(string)
  default     = []
}

# Slack cannot be fully Terraform-managed: google_monitoring_notification_channel type "slack"
# requires the "Google Cloud Monitoring" Slack app to be OAuth-authorized inside the target
# workspace first, via the GCP Console (Monitoring > Alerting > Edit notification channels >
# Slack > Add new) — a one-time manual step. Creating a Slack channel from a raw incoming-webhook
# URL via Terraform no longer works (hashicorp/terraform-provider-google#14256, still open).
#
# So: do that Console step once, find the resulting channel's numeric ID (Cloud Console shows it
# in the channel's URL/details, or `gcloud alpha monitoring channels list`), and set
# alert_slack_channel_id below. Until then this stays off.
variable "enable_slack_alerts" {
  description = "Attach the Slack notification channel to the OOM alert policy. Independent of alert_slack_channel_id so Slack can be muted without clearing the ID"
  type        = bool
  default     = false
}

variable "alert_slack_channel_id" {
  description = "Numeric ID of a Slack notification channel already created via the manual GCP Console OAuth step (see comment above). Empty disables Slack regardless of enable_slack_alerts"
  type        = string
  default     = ""
}

variable "alerting_labels" {
  description = "Labels for alerting resources"
  type        = map(string)
  default     = {}
}

# ── MedGemma public-exposure alerting ─────────────────────────────────────────
#
# PR #238 moved MedGemma's authorisation from network position (internal-only ingress) to IAM, and
# said in as many words that the control making an accidental allUsers grant *impossible* rather
# than merely discouraged was the constraints/iam.allowedPolicyMemberDomains org policy.
#
# That control does not exist and cannot be created. This GCP project has no organization, and
# Domain Restricted Sharing allow-lists Cloud Identity customer IDs — with no organization there is
# no directory to name, so the constraint is meaningless rather than merely unset. VPC Service
# Controls and the org tiers of Security Command Center are unavailable for the same reason. That
# is a standing accepted risk, not a pending task: nothing at the platform level can stop someone
# adding allUsers back to the medical model, so the residual control is noticing quickly.
#
# Hence detection. Cloud Run Admin Activity audit logs are written by default and cannot be
# disabled, so this needs no Data Access logging and adds no ingest cost.
#
# Two limits worth stating rather than implying coverage this does not have:
#   - It watches the *service's own* IAM policy. Granting roles/run.invoker to allUsers at the
#     project level would achieve the same exposure and would not match this filter.
#   - It is detective, not preventive. The grant takes effect immediately; the alert follows.
#
# An IAM deny policy on run.services.setIamPolicy may be able to make this preventive even without
# an organization — deny policies attach to projects too. Unverified on both counts (org-less
# projects, and whether that permission is supported), so it is a lead, not a plan.
variable "enable_medgemma_iam_alerting" {
  description = "Create the log-match alert policy that fires when MedGemma's IAM policy is changed to grant allUsers or allAuthenticatedUsers. Only meaningful where MedGemma is deployed"
  type        = bool
  default     = true

  validation {
    # Same trap as enable_oom_alerting: a policy with no channel fires into the void.
    #
    # Short-circuits on an empty medgemma_image so the check matches the resource gating below.
    # Without that, an environment with no MedGemma — prod today — could fail to plan over a
    # missing channel for an alert that would never have been created. Reachable in practice only
    # by turning enable_oom_alerting off (its identical validation fires first otherwise), which is
    # exactly the narrow case where a spurious failure would be most confusing.
    condition = (
      !var.enable_medgemma_iam_alerting ||
      var.medgemma_image == "" ||
      length(var.alert_notification_emails) > 0 ||
      (var.enable_slack_alerts && var.alert_slack_channel_id != "")
    )
    error_message = "enable_medgemma_iam_alerting requires at least one notification channel: set alert_notification_emails, or enable_slack_alerts with a non-empty alert_slack_channel_id."
  }
}

# ── Public certificate expiry alerting ────────────────────────────────────────
#
# app.dev.carditrack.com's managed certificate expired on 2026-08-07 and stayed dead for six days.
# A Google-managed certificate renews only while its domain still validates; app.dev's DNS record
# was lost some time after issuance, renewal failed quietly, and nothing anywhere noticed. It
# surfaced because someone probed the domain by hand while checking something else.
#
# The deploy-time smoke tests catch this only when a deploy happens to run, and only for the
# service being deployed. A certificate dies on its own schedule — api.dev's runs to 2026-10-01 and
# webhook.dev's to its own date — so the check has to be continuous and cover every domain, not
# just whichever one someone last shipped.
#
# Uptime checks are the mechanism because their SSL metric is what is actually wanted:
# time_until_ssl_cert_expires is reported off the TLS handshake, so it keeps reporting even when
# Cloud Armor rejects the request body — which it does for unfamiliar user agents. An HTTP-level
# check would conflate "certificate is fine but the WAF said 403" with "certificate is dying".
#
# Twenty days is chosen against the renewal window, not plucked: Google renews a managed
# certificate roughly a month before expiry, so anything still under twenty days has already failed
# to renew at least once. Earlier would alert on healthy certificates mid-renewal.
variable "enable_cert_expiry_alerting" {
  description = "Create uptime checks for the configured public domains and alert when a TLS certificate is close to expiry. Catches a managed certificate that has silently stopped renewing"
  type        = bool
  default     = true

  validation {
    condition = (
      !var.enable_cert_expiry_alerting ||
      length(var.alert_notification_emails) > 0 ||
      (var.enable_slack_alerts && var.alert_slack_channel_id != "")
    )
    error_message = "enable_cert_expiry_alerting requires at least one notification channel: set alert_notification_emails, or enable_slack_alerts with a non-empty alert_slack_channel_id."
  }
}

variable "cert_expiry_alert_days" {
  description = "Fire when a public domain's TLS certificate has fewer than this many days left. Should stay below Google's ~30-day managed-certificate renewal window so a certificate mid-renewal does not alert"
  type        = number
  default     = 20
}

variable "medgemma_iam_alert_name" {
  description = "Name for the MedGemma IAM alert policy, environment-qualified (dev and prod share one GCP project, so this must be unique per environment)"
  type        = string
}

# Resources

resource "google_logging_metric" "cloud_run_oom" {
  count = var.enable_oom_alerting ? 1 : 0
  name  = var.oom_alert_name

  # Matches the exact incident log line ("Memory limit of 512 MiB exceeded with 516 MiB used...")
  # and generalizes to any Cloud Run service in this environment, not just the worker.
  filter = "resource.type=\"cloud_run_revision\" AND resource.labels.service_name=~\"^${var.oom_alert_service_prefix}\" AND severity=ERROR AND textPayload:\"Memory limit of\""

  metric_descriptor {
    metric_kind = "DELTA"
    value_type  = "INT64"
    unit        = "1"
  }

  depends_on = [google_project_service.logging]
}

resource "google_monitoring_notification_channel" "oom_email" {
  for_each     = local.alert_channels_enabled ? toset(var.alert_notification_emails) : toset([])
  display_name = "${var.oom_alert_name}-email-${each.value}"
  type         = "email"
  labels = {
    email_address = each.value
  }
  user_labels = var.alerting_labels

  depends_on = [google_project_service.monitoring]
}

locals {
  # google_monitoring_notification_channel.id is already the full "projects/.../notificationChannels/..."
  # resource name; the Slack channel is referenced the same way even though Terraform doesn't manage it.
  # Both alert policies draw on these channels, so they have to exist if *either* is enabled.
  # Gating them on enable_oom_alerting alone meant that turning OOM alerting off silently stripped
  # the MedGemma policy's channels: an alert policy with nobody attached, which is the failure this
  # file's header calls out, reached without tripping either validation because the email list was
  # still populated. The OOM policy itself remains count-gated on its own flag, so nothing about
  # OOM alerting changes — only whether the shared channels exist.
  alert_channels_enabled = var.enable_oom_alerting || local.medgemma_iam_alerting

  oom_slack_channel_ids = (
    local.alert_channels_enabled && var.enable_slack_alerts && var.alert_slack_channel_id != ""
    ? ["projects/${var.project_id}/notificationChannels/${var.alert_slack_channel_id}"]
    : []
  )
}

resource "google_monitoring_alert_policy" "cloud_run_oom" {
  count        = var.enable_oom_alerting ? 1 : 0
  display_name = var.oom_alert_name
  combiner     = "OR"

  conditions {
    display_name = "Cloud Run OOM log entry"
    condition_threshold {
      filter          = "resource.type=\"cloud_run_revision\" AND metric.type=\"logging.googleapis.com/user/${google_logging_metric.cloud_run_oom[0].name}\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0
      # Fires on the first occurrence rather than waiting for it to persist — an OOM is rare and
      # high-severity, not noise worth debouncing, and by the time it repeats the process may
      # already be crash-looping.
      duration = "0s"

      aggregations {
        alignment_period   = "300s"
        per_series_aligner = "ALIGN_COUNT"
      }
    }
  }

  notification_channels = concat(
    [for c in google_monitoring_notification_channel.oom_email : c.id],
    local.oom_slack_channel_ids,
  )

  documentation {
    content   = "A Cloud Run container was OOM-killed. Left unaddressed, this silently kills any cron loop the process was running until the next redeploy replaces the instance (see incident: github.com/Codesistance/product-carditrack/issues/171). Check Cloud Logging for the affected service/revision and Cloud Monitoring's container memory metrics around the event time."
    mime_type = "text/markdown"
  }

  user_labels = var.alerting_labels

  depends_on = [google_project_service.monitoring]
}

# ── MedGemma public-exposure resources ────────────────────────────────────────

locals {
  # Gated on the service existing as well as the flag: prod leaves medgemma_image empty, and an
  # alert watching a service that was never created is noise in the console and a lie in the docs.
  medgemma_iam_alerting = var.enable_medgemma_iam_alerting && var.medgemma_image != ""

  # Matched on the audit log's own fields rather than resource.type, which differs between the v1
  # and v2 Cloud Run surfaces. methodName is regex-matched for the same reason: the Console, gcloud
  # and Terraform do not agree on which version they call.
  #
  # allAuthenticatedUsers is listed separately and deliberately — it does not contain "allUsers" as
  # a substring, so a single term would miss the more insidious of the two. It reads as restrictive
  # and means any Google account anywhere.
  #
  # Not filtered on severity: a successful SetIamPolicy is an INFO-level audit record, so adding a
  # severity term would silently match nothing.
  #
  # `:` (has), not `=`. members is a repeated field nested in a Struct payload, and the has-operator
  # is the one with predictable behaviour across repeated elements — an equality test that quietly
  # matches nothing is the worst outcome for a detection control, since it looks identical to "no
  # incident". Substring matching is safe here precisely because allAuthenticatedUsers does not
  # contain allUsers.
  #
  # Both request and response are checked. The request is what was asked for, the response what the
  # policy became; either alone would depend on which the Cloud Run surface populates, and this
  # filter has no second chance to be right — it only ever runs when something has already gone
  # wrong.
  medgemma_public_iam_filter = <<-EOT
    logName="projects/${var.project_id}/logs/cloudaudit.googleapis.com%2Factivity"
    AND protoPayload.serviceName="run.googleapis.com"
    AND protoPayload.methodName=~"SetIamPolicy"
    AND protoPayload.resourceName=~"${var.medgemma_service_name}$"
    AND (
      protoPayload.request.policy.bindings.members:"allUsers"
      OR protoPayload.request.policy.bindings.members:"allAuthenticatedUsers"
      OR protoPayload.response.bindings.members:"allUsers"
      OR protoPayload.response.bindings.members:"allAuthenticatedUsers"
    )
  EOT
}

resource "google_monitoring_alert_policy" "medgemma_public_iam" {
  count        = local.medgemma_iam_alerting ? 1 : 0
  display_name = var.medgemma_iam_alert_name
  combiner     = "OR"

  # condition_matched_log, not a metric threshold over a log-based metric.
  #
  # The first attempt used the OOM pattern — google_logging_metric plus condition_threshold — and
  # the API rejected the policy outright: "must specify a restriction on resource.type". A metric
  # threshold filters time series, and time series are always scoped to a monitored resource. That
  # is fine for the OOM alert, whose entries are unambiguously cloud_run_revision, but a Cloud Run
  # SetIamPolicy audit entry could land on cloud_run_revision or on audited_resource, and guessing
  # wrong would produce a policy that is valid, silent, and indistinguishable from "no incident" —
  # the failure mode this whole control exists to avoid.
  #
  # A log-match condition takes the log filter directly, so there is nothing to guess. It also
  # removes the log-based metric entirely: one resource instead of two, and no mapping between
  # them to be wrong about.
  conditions {
    display_name = "MedGemma IAM policy granted public access"
    condition_matched_log {
      filter = local.medgemma_public_iam_filter
    }
  }

  # Mandatory for log-match conditions — the API rejects the policy without it. Deliberately short:
  # this fires per matching entry, and a grant being applied repeatedly is itself worth seeing.
  alert_strategy {
    notification_rate_limit {
      period = "300s"
    }
  }

  # Reuses the OOM channels rather than creating duplicates for the same addresses. The resource
  # name is historical — it predates this alert, and renaming it would churn state for nothing.
  # Their creation is gated on local.alert_channels_enabled, not on enable_oom_alerting, precisely
  # so this policy cannot end up with an empty channel list.
  notification_channels = concat(
    [for c in google_monitoring_notification_channel.oom_email : c.id],
    local.oom_slack_channel_ids,
  )

  documentation {
    content   = <<-EOT
      **MedGemma's IAM policy now grants `allUsers` or `allAuthenticatedUsers`.**

      Since PR #238 the medical inference service is `INGRESS_TRAFFIC_ALL` and authorised purely by
      `roles/run.invoker` on two named identities. Internal-only ingress used to contain this
      mistake; it no longer does, and this project has no organization, so Domain Restricted Sharing
      cannot prevent it either. IAM is the only boundary, and it has just been opened.

      Anyone on the internet can now run inference against the model. MedGemma holds no data at rest
      — prompts carry age, sex, notes and readings but never name or id — so this is theft of
      inference capability plus whatever an attacker chooses to submit, not a read path into stored
      wearer data. It is also an uncapped bill against a warm instance.

      Remove the binding, then find out how it was added. If it came from the Console, that is the
      real finding: IAM here is Terraform-owned, and the next apply would have reverted it silently.
    EOT
    mime_type = "text/markdown"
  }

  user_labels = var.alerting_labels

  depends_on = [google_project_service.monitoring]
}

# ── Public certificate expiry resources ───────────────────────────────────────

locals {
  # Reuses load_balancer.tf's list, so a domain added there is watched here automatically rather
  # than needing to be remembered in two places — a domain nobody was looking at is what started
  # all this.
  cert_expiry_domains = var.enable_cert_expiry_alerting ? toset(local.configured_domains) : toset([])
}

resource "google_monitoring_uptime_check_config" "public_domain" {
  for_each = local.cert_expiry_domains

  display_name = "${local.lb_name_prefix}-${replace(each.value, ".", "-")}"
  timeout      = "10s"
  period       = "300s"

  http_check {
    path         = "/"
    port         = 443
    use_ssl      = true
    validate_ssl = true
  }

  monitored_resource {
    type = "uptime_url"
    labels = {
      project_id = var.project_id
      host       = each.value
    }
  }

  depends_on = [google_project_service.monitoring]
}

resource "google_monitoring_alert_policy" "cert_expiry" {
  count        = length(local.cert_expiry_domains) > 0 ? 1 : 0
  display_name = "${local.lb_name_prefix}-tls-cert-expiry"
  combiner     = "OR"

  conditions {
    display_name = "TLS certificate expiring on a public domain"
    condition_threshold {
      # resource.type is mandatory on a metric-threshold filter — omitting it is rejected outright
      # by the API, which is how the MedGemma alert first failed. uptime_url is the resource type
      # uptime checks report against.
      filter = join(" AND ", [
        "metric.type=\"monitoring.googleapis.com/uptime_check/time_until_ssl_cert_expires\"",
        "resource.type=\"uptime_url\"",
      ])
      comparison      = "COMPARISON_LT"
      threshold_value = var.cert_expiry_alert_days

      # An hour of confirmation before firing. The metric moves in days, so there is nothing to be
      # gained from reacting to a single scrape, and one checker region briefly failing a handshake
      # should not page anyone.
      duration = "3600s"

      aggregations {
        alignment_period = "3600s"
        # MIN across checker regions: if any region sees an expiring certificate, that is the
        # answer worth having. Averaging would let healthy regions mask a bad one.
        per_series_aligner   = "ALIGN_MIN"
        cross_series_reducer = "REDUCE_MIN"
        group_by_fields      = ["resource.label.host"]
      }
    }
  }

  notification_channels = concat(
    [for c in google_monitoring_notification_channel.oom_email : c.id],
    local.oom_slack_channel_ids,
  )

  documentation {
    content   = <<-EOT
      **A public domain's TLS certificate is close to expiry.**

      Google renews a managed certificate roughly a month out, so a certificate under
      ${var.cert_expiry_alert_days} days has already failed to renew at least once. Renewal fails
      when the domain stops validating — almost always because its DNS record no longer resolves to
      the load balancer IP.

      Check, in this order:

      1. `gcloud compute ssl-certificates list --global` — look for `PROVISIONING` with a
         `FAILED_NOT_VISIBLE` domain status.
      2. That the domain resolves to the load balancer address, and that the DNS record is
         **DNS-only** rather than proxied. A proxied record resolves to the provider's IP, and
         validation then never sees the load balancer.

      Fixing DNS does not un-expire a certificate that has already lapsed. Once the record is
      correct, force a reissue by bumping the generation suffix on the certificate resource
      (`web_cert_generation` in load_balancer.tf), which recreates it create-before-destroy.

      This alert exists because app.dev.carditrack.com expired on 2026-08-07 and nobody knew for
      six days.
    EOT
    mime_type = "text/markdown"
  }

  user_labels = var.alerting_labels

  depends_on = [google_project_service.monitoring]
}
