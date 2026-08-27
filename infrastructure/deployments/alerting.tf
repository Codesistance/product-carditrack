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

# The metric's unit is days, not seconds — Google's own examples use thresholds of 30/14/7. Worth
# stating because the value looks bare in the alert filter and "surely that's seconds" is the
# obvious wrong guess; reading it as seconds would make this fire twenty seconds before expiry,
# which is to say never usefully, and silently.
variable "cert_expiry_alert_days" {
  description = "Fire when a public domain's TLS certificate has fewer than this many days left. The metric is denominated in days. Keep below Google's ~30-day managed-certificate renewal window so a certificate mid-renewal does not alert"
  type        = number
  default     = 20
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
  # Gating them on enable_oom_alerting alone once meant that turning OOM alerting off silently
  # stripped another policy's channels (the since-moved MedGemma IAM alert): an alert policy with
  # nobody attached, which is the failure this file's header calls out, reached without tripping
  # either validation because the email list was still populated. The OOM policy itself remains
  # count-gated on its own flag, so nothing about OOM alerting changes — only whether the shared
  # channels exist.
  # Every policy that borrows these channels has to appear in this expression. That is easy to
  # forget — the cert-expiry policy was added reusing them and this line was not extended, which
  # would have left it notifying nobody in exactly the configuration where it mattered. If you add
  # a third alert here, add it below too.
  alert_channels_enabled = (
    var.enable_oom_alerting ||
    length(local.cert_expiry_domains) > 0
  )

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
