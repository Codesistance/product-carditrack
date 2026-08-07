# Cloud Pub/Sub
# Provides real-time event streaming between services
# For a .NET SignalR backplane, pair with Memorystore (see redis.tf)

# Variables
variable "pubsub_topic_name" {
  description = "Name of the Pub/Sub topic"
  type        = string
}

variable "enable_pubsub" {
  description = "Enable Cloud Pub/Sub for real-time messaging"
  type        = bool
  default     = false
}

variable "pubsub_region" {
  description = "Region to restrict Pub/Sub message storage"
  type        = string
}

variable "pubsub_labels" {
  description = "Labels for Pub/Sub resources"
  type        = map(string)
  default     = {}
}

# Resources
resource "google_pubsub_topic" "realtime" {
  count  = var.enable_pubsub ? 1 : 0
  name   = var.pubsub_topic_name
  labels = var.pubsub_labels

  message_storage_policy {
    allowed_persistence_regions = [var.pubsub_region]
  }

  depends_on = [google_project_service.pubsub]
}

resource "google_pubsub_subscription" "realtime" {
  count = var.enable_pubsub ? 1 : 0
  name  = "${var.pubsub_topic_name}-sub"
  topic = google_pubsub_topic.realtime[0].name

  ack_deadline_seconds = 30
  labels               = var.pubsub_labels
}
