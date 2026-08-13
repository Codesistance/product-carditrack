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
  api_service_name         = "${var.project_name}-${local.environment}-api"
  web_service_name         = "${var.project_name}-${local.environment}-web"
  worker_service_name      = "${var.project_name}-${local.environment}-worker"
  pipeline_jobs_name       = "${var.project_name}-${local.environment}-pipeline-jobs"
  webhook_receiver_name    = "${var.project_name}-${local.environment}-webhook-receiver"
  cloud_sql_name           = "${var.project_name}-${local.environment}-sql"
  redis_instance_name      = "${var.project_name}-${local.environment}-redis"
  cloud_sql_db_name        = "${var.project_name}-${local.environment}-db"
  storage_bucket_name      = "${var.project_id}-${var.project_name}-${local.environment}"
  pubsub_topic_name        = "${var.project_name}-${local.environment}-realtime"
  log_sink_name            = "${var.project_name}-${local.environment}-audit-sink"
  audit_bucket_name        = "${var.project_id}-${var.project_name}-${local.environment}-audit"
  oom_alert_name           = "${var.project_name}-${local.environment}-cloud-run-oom"
  oom_alert_service_prefix = "${var.project_name}-${local.environment}-"
  medgemma_iam_alert_name  = "${var.project_name}-${local.environment}-medgemma-public-iam"

  # Read rather than repeated: .model-version is what bakes a tag into the MedGemma image, and
  # AI__Private__Model below is the name the API then asks Ollama for. As two literals they
  # drift the moment one is bumped alone, and every medical call 404s against a model the server
  # never pulled. trimspace matches the `tr -d '[:space:]'` the image build applies to the file.
  medgemma_model = trimspace(file("${path.module}/../src/Infrastructure/MedGemma/.model-version"))

  # Defaults to the secret that already exists, so swapping the public provider is a tfvar change
  # rather than a destroy-and-recreate of a Secret Manager secret (and the seeding that follows it).
  public_ai_api_key_secret_id = coalesce(
    var.public_ai_api_key_secret_id,
    "${var.project_name}-${local.environment}-gemini-api-key"
  )

  # DeviceProviders blocks flattened onto the positional binding the apps already use for provider
  # secrets. Identity (HealthApi name + the DeviceTypes it serves), endpoints, scopes and cadence
  # all come from tfvars, so a deployed environment's provider config is set here — appsettings.json
  # keeps local-dev defaults only. List-valued settings override element-wise by index, so tfvars
  # lists must be complete, not deltas; optional endpoint fields emit nothing when null, leaving the
  # appsettings value in effect.
  device_pull_env_vars = merge([
    for i, p in var.device_pull_params : merge(
      {
        "DeviceProviders__${i}__Provider"               = p.provider
        "DeviceProviders__${i}__SyncLookbackDays"       = tostring(p.sync_lookback_days)
        "DeviceProviders__${i}__BackfillDays"           = tostring(p.backfill_days)
        "DeviceProviders__${i}__BackfillChunkDays"      = tostring(p.backfill_chunk_days)
        "DeviceProviders__${i}__AuditLookbackDays"      = tostring(p.audit_lookback_days)
        "DeviceProviders__${i}__MinPullIntervalMinutes" = tostring(p.min_pull_interval_minutes)
        "DeviceProviders__${i}__MaxPullIntervalMinutes" = tostring(p.max_pull_interval_minutes)
        "DeviceProviders__${i}__MaxRequestsPerSecond"   = tostring(p.max_requests_per_second)
        "DeviceProviders__${i}__DormancyThresholdPulls" = tostring(p.dormancy_threshold_pulls)
        "DeviceProviders__${i}__DormancyBackoffFactor"  = tostring(p.dormancy_backoff_factor)
      },
      { for j, t in p.device_types : "DeviceProviders__${i}__DeviceTypes__${j}" => t },
      { for j, sc in p.scopes : "DeviceProviders__${i}__Scopes__${j}" => sc },
      { for k, v in p.additional_authorization_params :
      "DeviceProviders__${i}__AdditionalAuthorizationParams__${k}" => v },
      { for k, v in p.first_consent_authorization_params :
      "DeviceProviders__${i}__FirstConsentAuthorizationParams__${k}" => v },
      p.authorization_url != null ? { "DeviceProviders__${i}__AuthorizationUrl" = p.authorization_url } : {},
      p.token_url != null ? { "DeviceProviders__${i}__TokenUrl" = p.token_url } : {},
      p.api_base_url != null ? { "DeviceProviders__${i}__ApiBaseUrl" = p.api_base_url } : {},
      p.token_lifetime_hours != null ? { "DeviceProviders__${i}__TokenLifetimeHours" = tostring(p.token_lifetime_hours) } : {}
    )
  ]...)
}

# All deployments are managed through a single module
module "deployments" {
  source = "./deployments"

  # Project / Region
  project_id = var.project_id
  region     = local.region

  # Cloud Run - Custom domains
  api_custom_domain     = var.api_custom_domain
  web_custom_domain     = var.web_custom_domain
  webhook_custom_domain = var.webhook_custom_domain

  # Cloud Run - Shared
  cloud_run_location      = local.region
  cloud_run_min_instances = local.is_prod ? 1 : 0
  cloud_run_max_instances = local.is_prod ? 3 : 1
  cloud_run_cpu           = var.cloud_run_cpu
  cloud_run_memory        = var.cloud_run_memory
  worker_cloud_run_memory = var.worker_cloud_run_memory
  cloud_run_labels        = local.common_labels

  # Cloud Run - API
  api_service_name    = local.api_service_name
  api_container_image = var.api_container_image
  api_env_vars = merge(
    {
      "ASPNETCORE_ENVIRONMENT"              = title(var.environment)
      "ASPNETCORE_FORWARDEDHEADERS_ENABLED" = "true"
      "GCP_PROJECT_ID"                      = var.project_id
      "AI__Public__Kind"                    = var.public_ai_kind
      "AI__Public__Model"                   = var.public_ai_model
      "AI__Public__TimeoutSeconds"          = tostring(var.public_ai_timeout_seconds)
      "AI__Public__MaxOutputTokens"         = tostring(var.public_ai_max_output_tokens)
      "AI__Private__Model"                  = local.medgemma_model
      "AI__Private__TimeoutSeconds"         = tostring(var.medgemma_timeout_seconds)
      # MedGemma authorises callers by IAM, so every request needs an OIDC token. Set for every host
      # that receives AI__Private__BaseUrl, including the aggregator, which carries the config
      # without calling the model — AiServiceExtensions refuses to start a host whose BaseUrl is a
      # Cloud Run URL while this is false, and that check runs wherever the settings are bound.
      "AI__Private__UseIdentityToken"  = "true"
      "Apm__Engine"                    = var.apm_engine
      "Apm__MetricsEnabled"            = tostring(var.apm_metrics_enabled)
      "Apm__TracesSampleRatio"         = tostring(var.traces_sample_ratio.api)
      "Serilog__MinimumLevel__Default" = var.log_minimum_level.api
    },
    # Transitional — DELETE once an image carrying the AI__Public/AI__Private settings is
    # deployed to every environment. Terraform sets env vars and CI sets the image
    # independently, so an apply that landed first would otherwise strip the settings the
    # running revision still reads and fail it at startup. The new image ignores these.
    {
      "AI__GeneralProvider"              = "Gemini"
      "AI__MedicalProvider"              = "MedGemma"
      "AI__Providers__0__Name"           = "MedGemma"
      "AI__Providers__0__Model"          = local.medgemma_model
      "AI__Providers__0__TimeoutSeconds" = tostring(var.medgemma_timeout_seconds)
      "AI__Providers__1__Name"           = "Gemini"
      "AI__Providers__1__BaseUrl"        = "https://generativelanguage.googleapis.com"
      "AI__Providers__1__Model"          = "gemini-2.0-flash"
    },
    # Omitted unless overridden, so each provider keeps its own documented endpoint.
    var.public_ai_base_url != null ? {
      "AI__Public__BaseUrl" = var.public_ai_base_url
    } : {},
    # The API runs the OAuth connect flow, so it needs the provider blocks' identity, endpoints
    # and scopes — not just the pull hosts' cadence numbers.
    local.device_pull_env_vars,
    # Google's web OAuth clients require an https redirect; the API bounces it to the app deep
    # link. Element 0 of DeviceProviders in appsettings.json is the GoogleHealth (Google Health API)
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
      # Push delivery spine (notification_engine.md Phase 3) — API issues and validates ack/
      # fetch tokens (the immediate-attempt send path + the /delivered endpoint), so it needs
      # this key. Not injected into the pipeline jobs below: they never send push directly.
      "Notifications__AckTokenKey"       = "${var.project_name}-${local.environment}-ack-token-key"
      "Health__Token"                    = "${var.project_name}-${local.environment}-health-token"
      "DeviceProviders__0__ClientId"     = "${var.project_name}-${local.environment}-devices-fitbit-client-id"
      "DeviceProviders__0__ClientSecret" = "${var.project_name}-${local.environment}-devices-fitbit-client-secret"
      "AI__Private__BaseUrl"             = "${var.project_name}-${local.environment}-medgemma-service-url"
      "AI__Public__ApiKey"               = local.public_ai_api_key_secret_id
      "Apm__Data"                        = "${var.project_name}-${local.environment}-apm-data"
      # Transitional — delete alongside the legacy AI__Providers env vars above.
      "AI__Providers__0__BaseUrl" = "${var.project_name}-${local.environment}-medgemma-service-url"
      "AI__Providers__1__ApiKey"  = local.public_ai_api_key_secret_id
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
    # NotificationDispatchWorker retries sends and issues fresh ack/fetch tokens per attempt.
    "Notifications__AckTokenKey"       = "${var.project_name}-${local.environment}-ack-token-key"
    "Health__Token"                    = "${var.project_name}-${local.environment}-health-token"
    "DeviceProviders__0__ClientId"     = "${var.project_name}-${local.environment}-devices-fitbit-client-id"
    "DeviceProviders__0__ClientSecret" = "${var.project_name}-${local.environment}-devices-fitbit-client-secret"
    "Apm__Data"                        = "${var.project_name}-${local.environment}-apm-data"
  }

  # Cloud Run - Pipeline jobs (AI pipeline; digest generation). Deliberately the narrowest env
  # set of any host: no Auth0, no device-provider credentials, and no public AI key — the job
  # holds only what a MedGemma call over Cloud SQL data needs, so it cannot reach anything else.
  enable_pipeline_jobs          = var.enable_pipeline_jobs
  pipeline_jobs_name            = local.pipeline_jobs_name
  pipeline_jobs_container_image = var.pipeline_jobs_container_image
  pipeline_jobs_env_vars = {
    "ASPNETCORE_ENVIRONMENT"         = title(var.environment)
    "GCP_PROJECT_ID"                 = var.project_id
    "AI__Private__Model"             = local.medgemma_model
    "AI__Private__TimeoutSeconds"    = tostring(var.medgemma_timeout_seconds)
    "AI__Private__UseIdentityToken"  = "true"
    "Apm__Engine"                    = var.apm_engine
    "Apm__MetricsEnabled"            = tostring(var.apm_metrics_enabled)
    "Serilog__MinimumLevel__Default" = var.log_minimum_level.worker
  }
  pipeline_jobs_secret_env_vars = {
    "ConnectionStrings__DefaultConnection" = "${var.project_name}-${local.environment}-db-connection-string"
    "Encryption__Key"                      = "${var.project_name}-${local.environment}-encryption-key"
    "AI__Private__BaseUrl"                 = "${var.project_name}-${local.environment}-medgemma-service-url"
    "Apm__Data"                            = "${var.project_name}-${local.environment}-apm-data"
  }

  # The aggregator runs the same targeted sync the Worker does, so it carries the Worker's
  # device-provider wiring (incl. OAuth client credentials for token refresh) on top of the
  # pipeline base — plus the subscription it drains. Still no public AI key.
  pipeline_aggregator_env_vars = merge(
    {
      "ASPNETCORE_ENVIRONMENT"         = title(var.environment)
      "GCP_PROJECT_ID"                 = var.project_id
      "AI__Private__Model"             = local.medgemma_model
      "AI__Private__TimeoutSeconds"    = tostring(var.medgemma_timeout_seconds)
      "AI__Private__UseIdentityToken"  = "true"
      "Apm__Engine"                    = var.apm_engine
      "Apm__MetricsEnabled"            = tostring(var.apm_metrics_enabled)
      "Serilog__MinimumLevel__Default" = var.log_minimum_level.worker
      "PubSub__ProjectId"              = var.project_id
      "PubSub__SubscriptionId"         = "${local.pubsub_topic_name}-sub"
    },
    local.device_pull_env_vars
  )
  pipeline_aggregator_secret_env_vars = {
    "ConnectionStrings__DefaultConnection" = "${var.project_name}-${local.environment}-db-connection-string"
    "Encryption__Key"                      = "${var.project_name}-${local.environment}-encryption-key"
    "AI__Private__BaseUrl"                 = "${var.project_name}-${local.environment}-medgemma-service-url"
    "Apm__Data"                            = "${var.project_name}-${local.environment}-apm-data"
    "DeviceProviders__0__ClientId"         = "${var.project_name}-${local.environment}-devices-fitbit-client-id"
    "DeviceProviders__0__ClientSecret"     = "${var.project_name}-${local.environment}-devices-fitbit-client-secret"
  }

  # Cloud Run - Health webhook receiver (public ingress; secret-authenticated). Console-only
  # logging for now — wiring it into APM shipping is a follow-up, and its Serilog output reaches
  # Cloud Logging regardless.
  enable_webhook_receiver          = var.enable_webhook_receiver
  webhook_receiver_name            = local.webhook_receiver_name
  webhook_receiver_container_image = var.webhook_receiver_container_image
  webhook_receiver_env_vars = {
    "ASPNETCORE_ENVIRONMENT" = title(var.environment)
    "GCP_PROJECT_ID"         = var.project_id
    "PubSub__ProjectId"      = var.project_id
    "PubSub__TopicId"        = local.pubsub_topic_name
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
  vpc_name         = "${var.project_name}-${local.environment}-vpc"
  subnet_name      = "${var.project_name}-${local.environment}-subnet"
  subnet_cidr      = var.subnet_cidr
  enable_cloud_nat = var.enable_cloud_nat

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

  # Firebase Cloud Messaging (push notification sending credentials)
  enable_push_notifications = var.enable_push_notifications

  # Cloud Monitoring & platform audit logging. The audit bucket reads the storage_location
  # passed above, so every bucket in the deployment lands in the same place.
  log_sink_name                 = local.log_sink_name
  audit_bucket_name             = local.audit_bucket_name
  enable_platform_audit_logging = var.enable_platform_audit_logging
  audit_retention_days          = var.audit_retention_days
  monitoring_labels             = local.common_labels

  # Cloud Run OOM alerting
  enable_oom_alerting          = var.enable_oom_alerting
  oom_alert_name               = local.oom_alert_name
  oom_alert_service_prefix     = local.oom_alert_service_prefix
  enable_medgemma_iam_alerting = var.enable_medgemma_iam_alerting
  enable_cert_expiry_alerting  = var.enable_cert_expiry_alerting
  cert_expiry_alert_days       = var.cert_expiry_alert_days
  medgemma_iam_alert_name      = local.medgemma_iam_alert_name
  alert_notification_emails    = var.alert_notification_emails
  enable_slack_alerts          = var.enable_slack_alerts
  alert_slack_channel_id       = var.alert_slack_channel_id
  alerting_labels              = local.common_labels

  # MedGemma (Ollama)
  medgemma_service_name  = "${var.project_name}-${local.environment}-medgemma"
  medgemma_image         = var.medgemma_image
  medgemma_cpu           = var.medgemma_cpu
  medgemma_memory        = var.medgemma_memory
  medgemma_max_instances = 1
  medgemma_min_instances = var.medgemma_min_instances
}
