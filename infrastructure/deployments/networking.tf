# Networking
# VPC network, subnet, and private services access for Cloud SQL private IP

# Variables
variable "vpc_name" {
  description = "Name of the VPC network"
  type        = string
}

variable "subnet_name" {
  description = "Name of the subnet"
  type        = string
}

variable "subnet_cidr" {
  description = "CIDR range for the subnet"
  type        = string
  default     = "10.0.0.0/24"
}

# Resources
resource "google_compute_network" "main" {
  name                    = var.vpc_name
  auto_create_subnetworks = false
  depends_on              = [google_project_service.compute]
}

resource "google_compute_subnetwork" "main" {
  name          = var.subnet_name
  ip_cidr_range = var.subnet_cidr
  region        = var.region
  network       = google_compute_network.main.id

  # Required for ALL_TRAFFIC-egress callers (api, pipeline_jobs, pipeline_assessor) to reach
  # MedGemma's internal-ingress-only *.run.app URL: without this, VPC-routed traffic from a
  # private-IP-only resource has no route to Google's public IPs at all.
  private_ip_google_access = true
}

# Reserved IP range for Cloud SQL private services access (VPC peering)
resource "google_compute_global_address" "private_services_range" {
  name          = "${var.vpc_name}-private-range"
  purpose       = "VPC_PEERING"
  address_type  = "INTERNAL"
  prefix_length = 16
  network       = google_compute_network.main.id
}

resource "google_service_networking_connection" "private_services" {
  network                 = google_compute_network.main.id
  service                 = "servicenetworking.googleapis.com"
  reserved_peering_ranges = [google_compute_global_address.private_services_range.name]
  depends_on              = [google_project_service.servicenetworking]
}
