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
}

resource "google_monitoring_notification_channel" "oom_email" {
  count        = var.enable_oom_alerting ? length(var.alert_notification_emails) : 0
  display_name = "${var.oom_alert_name}-email-${count.index}"
  type         = "email"
  labels = {
    email_address = var.alert_notification_emails[count.index]
  }
  user_labels = var.alerting_labels
}

locals {
  # google_monitoring_notification_channel.id is already the full "projects/.../notificationChannels/..."
  # resource name; the Slack channel is referenced the same way even though Terraform doesn't manage it.
  oom_slack_channel_ids = (
    var.enable_oom_alerting && var.enable_slack_alerts && var.alert_slack_channel_id != ""
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
    google_monitoring_notification_channel.oom_email[*].id,
    local.oom_slack_channel_ids,
  )

  documentation {
    content   = "A Cloud Run container was OOM-killed. Left unaddressed, this silently kills any cron loop the process was running until the next redeploy replaces the instance (see incident: github.com/Codesistance/product-carditrack/issues/171). Check Cloud Logging for the affected service/revision and Cloud Monitoring's container memory metrics around the event time."
    mime_type = "text/markdown"
  }

  user_labels = var.alerting_labels

  depends_on = [google_project_service.monitoring]
}
