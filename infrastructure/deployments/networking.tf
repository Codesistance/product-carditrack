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

  # Still required, but no longer for MedGemma. The original reason was ALL_TRAFFIC-egress callers
  # reaching MedGemma's internal-ingress *.run.app URL through the VPC; those callers are now
  # PRIVATE_RANGES_ONLY and authenticate to MedGemma by IAM instead, so that path is gone.
  #
  # What still depends on this: the Cloud SQL Auth Proxy. Dev's instance has no public IP at all
  # (cloud_sql_public_ip_enabled = false), so the private-IP route is the only route. Removing this
  # breaks the database, not just an optimisation — it is not dead config despite the MedGemma
  # rationale having lapsed.
  private_ip_google_access = true
}

# Cloud NAT — retained behind a flag purely to make its removal a separate, reversible apply.
#
# It exists only because api/pipeline_jobs/pipeline_assessor used to run ALL_TRAFFIC egress to reach
# MedGemma, which forced *every* outbound call — Auth0 (ServiceCollectionExtensions.cs), Datadog APM
# (DatadogApmProvider.cs) — through the VPC, where Private Google Access does not help because those
# are not Google endpoints. Those three services are now PRIVATE_RANGES_ONLY, so their internet
# traffic leaves directly and nothing routes through NAT any more. It is a fixed ~£24/month charge
# for an unused gateway.
#
# Why a flag rather than deleting the resources outright: Terraform does not order a destroy against
# unrelated in-place updates, so a single apply could tear the gateway down before the egress changes
# land on all three services — a window with no internet egress at all. Two applies remove that
# window entirely:
#
#   1. Apply with enable_cloud_nat = true (the default). Egress, ingress, IAM and the new runtime
#      service accounts all move while NAT is still there to catch anything mid-flight. Verify the
#      services are healthy and MedGemma calls succeed.
#   2. Set enable_cloud_nat = false in the environment's tfvars and apply again. That is the change
#      that actually saves the money, and it reverts by flipping one boolean back.
#
# Before step 2, confirm nothing downstream pins our egress IP. NAT gives a stable, known source
# address; direct Cloud Run egress uses Google's shared pool. If Auth0 tenant IP restrictions, a
# Datadog allowlist, or the Google Health API are ever configured against a fixed address, step 2
# breaks them silently — and NAT logs disappear with the gateway too.
variable "enable_cloud_nat" {
  description = "Provision Cloud NAT and its router. Only needed while a service uses ALL_TRAFFIC egress; with every service on PRIVATE_RANGES_ONLY nothing routes through it. Set false to retire the gateway (~£24/month) once the egress changes are verified — see the note in networking.tf"
  type        = bool
  default     = true
}

resource "google_compute_router" "main" {
  count   = var.enable_cloud_nat ? 1 : 0
  name    = "${var.vpc_name}-router"
  region  = var.region
  network = google_compute_network.main.id
}

resource "google_compute_router_nat" "main" {
  count                              = var.enable_cloud_nat ? 1 : 0
  name                               = "${var.vpc_name}-nat"
  router                             = google_compute_router.main[0].name
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
