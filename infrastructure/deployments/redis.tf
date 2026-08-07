# Memorystore for Redis
# Backs IDistributedCache in the API: OAuth PKCE state during device linking and the
# 1-hour report cache. Both need to be shared across Cloud Run instances — the authorize
# and callback legs of an OAuth flow routinely land on different ones.

# Variables
variable "enable_redis" {
  description = "Provision a Memorystore for Redis instance and bind it to the API"
  type        = bool
  default     = false
}

variable "redis_instance_name" {
  description = "Name of the Memorystore for Redis instance"
  type        = string
}

variable "redis_tier" {
  description = "Memorystore service tier (BASIC for a standalone instance, STANDARD_HA for primary/replica)"
  type        = string
  default     = "BASIC"
}

variable "redis_memory_size_gb" {
  description = "Memorystore capacity in GB"
  type        = number
  default     = 1
}

variable "redis_version" {
  description = "Memorystore Redis engine version"
  type        = string
  default     = "REDIS_7_2"
}

variable "redis_labels" {
  description = "Labels for Memorystore resources"
  type        = map(string)
  default     = {}
}

# Resources
resource "google_redis_instance" "main" {
  count          = var.enable_redis ? 1 : 0
  name           = var.redis_instance_name
  tier           = var.redis_tier
  memory_size_gb = var.redis_memory_size_gb
  redis_version  = var.redis_version
  region         = var.region
  labels         = var.redis_labels

  authorized_network = google_compute_network.main.id

  # Reuses the /16 already allocated for Cloud SQL private services access
  # (networking.tf) — Memorystore carves a /29 out of it. Private services access
  # rather than direct peering because that route from Cloud Run into this VPC is
  # the one Cloud SQL already proves works.
  connect_mode      = "PRIVATE_SERVICE_ACCESS"
  reserved_ip_range = google_compute_global_address.private_services_range.name

  # AUTH string + TLS. Enabling in-transit encryption moves the endpoint from 6379
  # to 6378; nothing here hardcodes the port, the connection string is built from
  # the instance's own `port` attribute (secret_manager.tf).
  auth_enabled            = true
  transit_encryption_mode = "SERVER_AUTHENTICATION"

  depends_on = [
    google_project_service.redis,
    google_service_networking_connection.private_services,
  ]
}
