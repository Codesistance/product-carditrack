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
  # MedGemma's internal-ingress-only *.run.app URL. Scope note: this covers Google-managed
  # front ends only (MedGemma's URL among them) — it is not general internet egress. Those
  # same callers also make real internet calls (Auth0, Datadog APM), which this does not
  # cover; see the Cloud NAT resources below for that.
  private_ip_google_access = true
}

# Cloud NAT — ALL_TRAFFIC egress (api, pipeline_jobs, pipeline_assessor) routes every
# outbound call through the VPC, not just the MedGemma one. Private Google Access above only
# reaches Google-managed endpoints; the API's Auth0 calls (ServiceCollectionExtensions.cs)
# and pipeline_jobs/pipeline_assessor's direct-to-Datadog APM shipping (DatadogApmProvider.cs)
# are ordinary internet destinations and need this to keep working.
resource "google_compute_router" "main" {
  name    = "${var.vpc_name}-router"
  region  = var.region
  network = google_compute_network.main.id
}

resource "google_compute_router_nat" "main" {
  name                               = "${var.vpc_name}-nat"
  router                             = google_compute_router.main.name
  region                             = var.region
  nat_ip_allocate_option             = "AUTO_ONLY"
  source_subnetwork_ip_ranges_to_nat = "ALL_SUBNETWORKS_ALL_IP_RANGES"
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
