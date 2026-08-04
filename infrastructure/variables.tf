# Root Variables for CardiTrack Infrastructure
# These are provided via environment-specific tfvars files

variable "project_id" {
  description = "GCP project ID"
  type        = string
}

variable "environment" {
  description = "Environment name (dev or prod)"
  type        = string
  validation {
    condition     = contains(["dev", "prod"], var.environment)
    error_message = "Environment must be dev or prod."
  }
}

variable "region" {
  description = "GCP region for resources"
  type        = string
  default     = "europe-west2"
}

variable "project_name" {
  description = "Project name for resource naming"
  type        = string
  default     = "carditrack"
}

# Database Configuration
variable "db_admin_username" {
  description = "Cloud SQL administrator username"
  type        = string
}

# Cloud Run Configuration
variable "cloud_run_cpu" {
  description = "CPU allocation for Cloud Run services"
  type        = string
  default     = "1"
}

variable "cloud_run_memory" {
  description = "Memory allocation for Cloud Run services"
  type        = string
  default     = "512Mi"
}

variable "api_container_image" {
  description = "Container image for the API service"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "web_container_image" {
  description = "Container image for the Web service"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Cloud SQL Configuration
variable "cloud_sql_edition" {
  description = "Cloud SQL edition (ENTERPRISE or ENTERPRISE_PLUS)"
  type        = string
  default     = "ENTERPRISE"
}

variable "cloud_sql_tier" {
  description = "Cloud SQL machine tier (db-f1-micro for dev, db-custom-2-7680 for prod)"
  type        = string
  default     = "db-f1-micro"
}

variable "cloud_sql_disk_size_gb" {
  description = "Cloud SQL disk size in GB"
  type        = number
  default     = 10
}

variable "cloud_sql_ha_enabled" {
  description = "Enable high availability for Cloud SQL (REGIONAL availability type)"
  type        = bool
  default     = false
}

variable "cloud_sql_deletion_protection" {
  description = "Enable deletion protection for Cloud SQL instance"
  type        = bool
  default     = false
}

variable "cloud_sql_public_ip_enabled" {
  description = "Enable public IP for Cloud SQL (should be false; use Cloud SQL Auth Proxy)"
  type        = bool
  default     = false
}

variable "migrator_container_image" {
  description = "Container image for the DB migrator Cloud Run Job"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "worker_container_image" {
  description = "Container image for the Worker service"
  type        = string
  default     = "us-docker.pkg.dev/cloudrun/container/hello"
}

# Storage Configuration
variable "storage_location" {
  description = "GCS bucket location (US, EU, ASIA, or specific region)"
  type        = string
  default     = "EU"
}

variable "api_custom_domain" {
  description = "Custom domain for the API Cloud Run service (e.g. api.carditrack.com)"
  type        = string
  default     = ""
}

variable "web_custom_domain" {
  description = "Custom domain for the Web Cloud Run service (e.g. app.carditrack.com)"
  type        = string
  default     = ""
}

variable "storage_class" {
  description = "GCS storage class (STANDARD, NEARLINE, COLDLINE, ARCHIVE)"
  type        = string
  default     = "STANDARD"
}

# APM Configuration (logs + traces shipping)
variable "apm_engine" {
  description = "APM backend for API/Web/Worker (must match an engine in ApmProviderRegistry, e.g. BetterStack); empty string disables shipping"
  type        = string
  default     = "BetterStack"
}

variable "apm_mobile_engine" {
  description = "Mobile monitoring engine stamped into app builds (must match an engine in the app's MobileApm registry, e.g. Datadog); empty string disables monitoring"
  type        = string
  default     = "Datadog"
}

variable "apm_metrics_enabled" {
  description = "Export OTel metrics (runtime, ASP.NET Core, HttpClient, Npgsql) from API/Web/Worker to the APM backend. Off by default: metrics bill as custom metrics and stream around the clock"
  type        = bool
  default     = false
}

# Pub/Sub Configuration (real-time messaging)
variable "enable_pubsub" {
  description = "Enable Cloud Pub/Sub for real-time messaging (production only)"
  type        = bool
  default     = false
}

# HIPAA Compliance Configuration
variable "enable_hipaa_compliance" {
  description = "Enable HIPAA compliance features (audit logging, enhanced retention)"
  type        = bool
  default     = false
}

variable "audit_retention_days" {
  description = "Audit log retention in days (90 for HIPAA)"
  type        = number
  default     = 90
}

# Labels
variable "additional_labels" {
  description = "Additional labels to apply to all resources"
  type        = map(string)
  default     = {}
}

# Networking
variable "subnet_cidr" {
  description = "CIDR range for the primary subnet"
  type        = string
  default     = "10.0.0.0/24"
}

# MedGemma (Ollama)
variable "medgemma_image" {
  description = "Container image for MedGemma (empty string disables the service)"
  type        = string
  default     = ""
}

variable "medgemma_cpu" {
  description = "CPU allocation for the MedGemma Cloud Run service"
  type        = string
  default     = "8"
}

variable "medgemma_memory" {
  description = "Memory allocation for the MedGemma Cloud Run service"
  type        = string
  default     = "16Gi"
}
