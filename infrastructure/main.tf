# CardiTrack Infrastructure - Main Orchestration
# This file orchestrates all deployment modules

locals {
  environment = var.environment
  region      = var.region
  is_prod     = var.environment == "prod"

  common_labels = merge(
    {
      environment = lower(var.environment)
      project     = "carditrack"
      managed_by  = "terraform"
      cost_center = "engineering"
    },
    var.additional_labels
  )

  # Resource naming
  api_service_name    = "${var.project_name}-${local.environment}-api"
  web_service_name    = "${var.project_name}-${local.environment}-web"
  worker_service_name = "${var.project_name}-${local.environment}-worker"
  cloud_sql_name      = "${var.project_name}-${local.environment}-sql"
  redis_instance_name = "${var.project_name}-${local.environment}-redis"
  cloud_sql_db_name   = "${var.project_name}-${local.environment}-db"
  storage_bucket_name = "${var.project_id}-${var.project_name}-${local.environment}"
  pubsub_topic_name   = "${var.project_name}-${local.environment}-realtime"
  log_sink_name       = "${var.project_name}-${local.environment}-audit-sink"
  audit_bucket_name   = "${var.project_id}-${var.project_name}-${local.environment}-audit"

  # Per-device-type pull parameters flattened onto the positional DeviceProviders binding the apps
  # already use for provider secrets. Whichever service hosts device pull reads them from here, so
  # cadence is retuned per environment in tfvars rather than in appsettings.json.
  device_pull_env_vars = merge([
    for i, p in var.device_pull_params : {
      "DeviceProviders__${i}__SyncLookbackDays"       = tostring(p.sync_lookback_days)
      "DeviceProviders__${i}__AuditLookbackDays"      = tostring(p.audit_lookback_days)
      "DeviceProviders__${i}__MinPullIntervalMinutes" = tostring(p.min_pull_interval_minutes)
      "DeviceProviders__${i}__MaxPullIntervalMinutes" = tostring(p.max_pull_interval_minutes)
      "DeviceProviders__${i}__MaxRequestsPerSecond"   = tostring(p.max_requests_per_second)
      "DeviceProviders__${i}__DormancyThresholdPulls" = tostring(p.dormancy_threshold_pulls)
      "DeviceProviders__${i}__DormancyBackoffFactor"  = tostring(p.dormancy_backoff_factor)
    }
  ]...)
}

# All deployments are managed through a single module
module "deployments" {
  source = "./deployments"

  # Project / Region
  project_id = var.project_id
  region     = local.region

  # Cloud Run - Custom domains
  api_custom_domain = var.api_custom_domain
  web_custom_domain = var.web_custom_domain

  # Cloud Run - Shared
  cloud_run_location      = local.region
  cloud_run_min_instances = local.is_prod ? 1 : 0
  cloud_run_max_instances = local.is_prod ? 3 : 1
  cloud_run_cpu           = var.cloud_run_cpu
  cloud_run_memory        = var.cloud_run_memory
  cloud_run_labels        = local.common_labels

  # Cloud Run - API
  api_service_name    = local.api_service_name
  api_container_image = var.api_container_image
  api_env_vars = merge(
    {
      "ASPNETCORE_ENVIRONMENT"              = title(var.environment)
      "ASPNETCORE_FORWARDEDHEADERS_ENABLED" = "true"
      "GCP_PROJECT_ID"                      = var.project_id
      "AI__GeneralProvider"                 = "Gemini"
      "AI__MedicalProvider"                 = "MedGemma"
      "AI__Providers__0__Name"              = "MedGemma"
      "AI__Providers__0__Model"             = "medgemma:4b"
      "AI__Providers__0__TimeoutSeconds"    = "120"
      "AI__Providers__1__Name"              = "Gemini"
      "AI__Providers__1__BaseUrl"           = "https://generativelanguage.googleapis.com"
      "AI__Providers__1__Model"             = "gemini-2.0-flash"
      "Apm__Engine"                         = var.apm_engine
      "Apm__MetricsEnabled"                 = tostring(var.apm_metrics_enabled)
      "Apm__TracesSampleRatio"              = tostring(var.traces_sample_ratio.api)
      "Serilog__MinimumLevel__Default"      = var.log_minimum_level.api
    },
    # Google's web OAuth clients require an https redirect; the API bounces it to the app deep
    # link. Element 0 of DeviceProviders in appsettings.json is the Fitbit (Google Health API)
    # provider. Without a custom domain the appsettings localhost default stays in effect.
    var.api_custom_domain != "" ? {
      "DeviceProviders__0__RedirectUri" = "https://${var.api_custom_domain}/api/v1/oauth/redirect/fitbit"
    } : {}
  )
  api_secret_env_vars = merge(
    {
      "ConnectionStrings__DefaultConnection" = "${var.project_name}-${local.environment}-db-connection-string"
      "Auth0__Domain"                        = "${var.project_name}-${local.environment}-auth0-domain"
      "Auth0__Audience"                      = "${var.project_name}-${local.environment}-auth0-audience"
      "Auth0__ClientId"                      = "${var.project_name}-${local.environment}-auth0-client-id"
      "Auth0__ClientSecret"                  = "${var.project_name}-${local.environment}-auth0-client-secret"
      "Encryption__Key"                      = "${var.project_name}-${local.environment}-encryption-key"
      "Health__Token"                        = "${var.project_name}-${local.environment}-health-token"
      "DeviceProviders__0__ClientId"         = "${var.project_name}-${local.environment}-devices-fitbit-client-id"
      "DeviceProviders__0__ClientSecret"     = "${var.project_name}-${local.environment}-devices-fitbit-client-secret"
      "AI__Providers__0__BaseUrl"            = "${var.project_name}-${local.environment}-medgemma-service-url"
      "AI__Providers__1__ApiKey"             = "${var.project_name}-${local.environment}-gemini-api-key"
      "Apm__Data"                            = "${var.project_name}-${local.environment}-apm-data"
    },
    # Both are Terraform-owned and only exist when the instance does. Without them the
    # API keeps the appsettings.json localhost default and every cache write times out.
    var.enable_redis ? {
      "ConnectionStrings__Redis" = "${var.project_name}-${local.environment}-redis-connection-string"
      "Redis__CaCertificate"     = "${var.project_name}-${local.environment}-redis-ca"
    } : {}
  )

  # Cloud Run - Worker
  worker_service_name    = local.worker_service_name
  worker_container_image = var.worker_container_image
  worker_env_vars = merge(
    {
      "ASPNETCORE_ENVIRONMENT"         = title(var.environment)
      "GCP_PROJECT_ID"                 = var.project_id
      "Apm__Engine"                    = var.apm_engine
      "Apm__MetricsEnabled"            = tostring(var.apm_metrics_enabled)
      "Apm__TracesSampleRatio"         = tostring(var.traces_sample_ratio.worker)
      "Serilog__MinimumLevel__Default" = var.log_minimum_level.worker
    },
    # The Worker hosts device pull and the audit pull today, so it is where the cadence
    # parameters land.
    local.device_pull_env_vars
  )
  worker_secret_env_vars = {
    "ConnectionStrings__DefaultConnection" = "${var.project_name}-${local.environment}-db-connection-string"
    "Auth0__Domain"                        = "${var.project_name}-${local.environment}-auth0-domain"
    "Auth0__Audience"                      = "${var.project_name}-${local.environment}-auth0-audience"
    "Auth0__ClientId"                      = "${var.project_name}-${local.environment}-auth0-client-id"
    "Auth0__ClientSecret"                  = "${var.project_name}-${local.environment}-auth0-client-secret"
    "Encryption__Key"                      = "${var.project_name}-${local.environment}-encryption-key"
    "Health__Token"                        = "${var.project_name}-${local.environment}-health-token"
    "DeviceProviders__0__ClientId"         = "${var.project_name}-${local.environment}-devices-fitbit-client-id"
    "DeviceProviders__0__ClientSecret"     = "${var.project_name}-${local.environment}-devices-fitbit-client-secret"
    "Apm__Data"                            = "${var.project_name}-${local.environment}-apm-data"
  }

  # Cloud Run - Web
  web_service_name    = local.web_service_name
  web_container_image = var.web_container_image
  web_env_vars = {
    "ASPNETCORE_ENVIRONMENT"         = title(var.environment)
    "Apm__Engine"                    = var.apm_engine
    "Apm__MetricsEnabled"            = tostring(var.apm_metrics_enabled)
    "Apm__TracesSampleRatio"         = tostring(var.traces_sample_ratio.web)
    "Serilog__MinimumLevel__Default" = var.log_minimum_level.web
  }
  web_secret_env_vars = {
    "Apm__Data" = "${var.project_name}-${local.environment}-apm-data"
  }

  # Networking
  vpc_name    = "${var.project_name}-${local.environment}-vpc"
  subnet_name = "${var.project_name}-${local.environment}-subnet"
  subnet_cidr = var.subnet_cidr

  # Cloud SQL (PostgreSQL)
  cloud_sql_instance_name       = local.cloud_sql_name
  cloud_sql_database_name       = local.cloud_sql_db_name
  cloud_sql_region              = local.region
  db_admin_username             = var.db_admin_username
  cloud_sql_edition             = var.cloud_sql_edition
  cloud_sql_tier                = var.cloud_sql_tier
  cloud_sql_disk_size_gb        = var.cloud_sql_disk_size_gb
  cloud_sql_ha_enabled          = var.cloud_sql_ha_enabled
  cloud_sql_deletion_protection = var.cloud_sql_deletion_protection
  cloud_sql_public_ip_enabled   = var.cloud_sql_public_ip_enabled
  cloud_sql_enable_audit        = var.enable_platform_audit_logging
  cloud_sql_labels              = local.common_labels
  migrator_container_image      = var.migrator_container_image

  # Cloud Storage
  storage_bucket_name   = local.storage_bucket_name
  storage_location      = var.storage_location
  storage_class         = var.storage_class
  storage_force_destroy = !local.is_prod
  storage_labels        = local.common_labels

  # Secret Manager
  db_password_secret_id  = "${var.project_name}-${local.environment}-db-password"
  secret_id_prefix       = "${var.project_name}-${local.environment}"
  secret_labels          = local.common_labels
  deploy_service_account = "carditrack-deploy@${var.project_id}.iam.gserviceaccount.com"
  apm_mobile_engine      = var.apm_mobile_engine

  # Memorystore for Redis (distributed cache — API only; the Worker has no IDistributedCache consumer)
  redis_instance_name  = local.redis_instance_name
  enable_redis         = var.enable_redis
  redis_tier           = var.redis_tier
  redis_memory_size_gb = var.redis_memory_size_gb
  redis_version        = var.redis_version
  redis_labels         = local.common_labels

  # Cloud Pub/Sub (real-time messaging)
  pubsub_topic_name = local.pubsub_topic_name
  pubsub_region     = local.region
  enable_pubsub     = var.enable_pubsub
  pubsub_labels     = local.common_labels

  # Cloud Monitoring & platform audit logging. The audit bucket reads the storage_location
  # passed above, so every bucket in the deployment lands in the same place.
  log_sink_name                 = local.log_sink_name
  audit_bucket_name             = local.audit_bucket_name
  enable_platform_audit_logging = var.enable_platform_audit_logging
  audit_retention_days          = var.audit_retention_days
  monitoring_labels             = local.common_labels

  # MedGemma (Ollama)
  medgemma_service_name  = "${var.project_name}-${local.environment}-medgemma"
  medgemma_image         = var.medgemma_image
  medgemma_cpu           = var.medgemma_cpu
  medgemma_memory        = var.medgemma_memory
  medgemma_max_instances = 1
}
