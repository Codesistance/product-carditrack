# CardiTrack API — Application Overview

## Overview

The CardiTrack API is a RESTful ASP.NET Core 10 Web API that serves as the backend for the CardiTrack platform. It handles authentication (Auth0 JWT validation), device integrations, health data processing, alert management, and family member coordination.

> **Endpoint documentation lives in the canonical API spec: [/docs/execution/backend/api/](../../execution/backend/api/readme.md).** This document covers only the application itself — stack, structure, configuration, and local development. The spec versions all routes under `/api/v1/`; today's controllers actually serve `api/[controller]` routes — see the routing note under [Project Structure](#project-structure).

## Technology Stack

- **.NET 10**: Core framework
- **ASP.NET Core 10**: Web API framework
- **Entity Framework Core (Npgsql)**: ORM for Cloud SQL **PostgreSQL 16** (transactional system of record — see [storage boundary](../../infrastructure.md#storage-boundary)); `Program.cs` disables `Npgsql.EnableLegacyTimestampBehavior` so all `timestamptz` values surface as UTC
- **Auth0**: Authentication — the API validates Auth0-issued JWTs; it does not issue tokens or store credentials (see [auth.md](../../execution/backend/api/auth.md))
- **Swagger/OpenAPI**: API documentation (non-production environments only)
- **FluentValidation**: Request validation (validators in `Validators/`, registered via `AddValidators()`)
- **Asp.Versioning**: URL API versioning (default `v1`, assumed when unspecified, versions reported)
- **AspNetCoreRateLimit**: IP-based rate limiting (in-memory store)
- **AutoMapper**: DTO ↔ entity mapping (assembly-scanned profiles)
- **CORS**: allow-list policy (`AllowSpecificOrigins`) driven by the `Cors:AllowedOrigins` config array
- **Serilog**: Structured logging (console; APM shipping when the engine is configured)
- **OpenTelemetry**: Tracing exported to the configured APM backend over OTLP; metrics opt-in (see below). MedGemma calls emit their own GenAI spans/metrics via the `CardiTrack.Ai` source — see the [APM setup runbook](../../technical/apm_setup_runbook.md)

### APM shipping (`CardiTrack.Observability`)

The APM backend is switchable via the `Apm` config section, consumed by the API, Web, and
Worker through `CardiTrack.Observability` (`AddApmShipping` for Serilog, `AddApmTracing` for OTel):

```json
"Apm": {
  "Engine": "Datadog",                // provider name; see ApmProviderRegistry
  "Data": {                           // connection details for the selected engine
    "IngestUrl": "",
    "IngestToken": "",
    "Extra": {}                       // optional provider-specific keys (region, dataset, ...)
  },
  "MinimumLogLevel": "Warning",
  "TracesSampleRatio": 1.0,
  "MetricsEnabled": false             // opt-in OTel metrics export
}
```

`Data` is accepted in two forms: the nested section above (appsettings), or — the deployment
contract — a **single JSON value**. Deployed, the whole config is four env vars:

- `Apm__Engine` — plaintext Terraform env var from the `apm_engine` tfvar (**`"Datadog"`**
  in dev and prod; appsettings leaves it empty, so local runs log to console only).
  Careful: the Terraform **variable default is `"BetterStack"`** — both tfvars override it to
  `Datadog`, so an environment that forgets to set `apm_engine` silently flips backend.
- `Apm__Data` — Secret Manager-backed (secret `carditrack-<env>-apm-data`), holding one JSON
  object; unknown keys land in `Extra` for provider-specific details. Per engine:
  - Datadog: `{"IngestUrl":"datadoghq.eu","IngestToken":"<api key>","TraceEndpoint":"https://<org otlp intake>"}`
    (`TraceEndpoint` optional — logs-only without it)
  - Better Stack: `{"IngestUrl":"s123456.eu-nbg-2.betterstackdata.com","IngestToken":"<source token>"}`
- `Apm__MetricsEnabled` — plaintext env var from the `apm_metrics_enabled` tfvar
  (**dev `true`, prod `false`** — metrics bill as custom metrics and stream continuously)
- `Apm__TracesSampleRatio` — plaintext env var from the `traces_sample_ratio` tfvar, which is
  an object with one optional attribute per service (`api`, `web`, `worker`), **`1.0`
  everywhere today**. Its sibling `log_minimum_level` sets `Serilog__MinimumLevel__Default`
  the same way (**`Warning` everywhere**), so either service can be tuned on its own.

The single-value form wins when both are present. Shipping is **disabled until the engine, URL,
and token are all real values** — `REPLACE_ME` placeholders count as unset. Provisioning:
[APM setup runbook](../../technical/apm_setup_runbook.md) + `scripts/set-apm-secrets.sh` (which
composes the JSON). An unknown `Engine` or malformed `Apm__Data` JSON fails startup loudly. To
support a new backend, implement `IApmProvider` and register it in `ApmProviderRegistry` —
nothing changes in the apps.

Volume control (enforced engine-independently in `ApmExtensions`):

- Only `MinimumLogLevel` and above (`Warning`) is shipped. `Serilog:MinimumLevel:Default` is
  also `Warning`, so below-Warning events don't reach the console either; raising the root
  level via `Serilog__MinimumLevel__Default` widens the console/Cloud Logging output without
  widening what ships. Below `Information` also needs `Logging__LogLevel__Default`, because the
  Microsoft.Extensions.Logging filter runs ahead of Serilog.
- Traces are head-sampled via `TracesSampleRatio` (`1.0` — full sampling); `/health` requests
  are never traced. The code fallback for an unconfigured host stays `0.2`.
- Metrics are **off by default** and exported only when `Apm:MetricsEnabled` is true — then the
  ASP.NET Core and HttpClient instrumentation meters plus the `System.Runtime` and `Npgsql`
  meters ship over OTLP.

All three services (API, Web, Worker) carry identical values for these — the API's earlier
`Information` / `1.0` deviation from the others is resolved.

### Service naming (`ApmServiceNames`)

Each host reports as its **app type**, lowercase — `api`, `web`, `worker` (the MAUI app is
separate, `carditrack-mobile`). One constant per host feeds both signals: `AddApmShipping`
sets it as the log sink's service field and `AddApmTracing` sets it as the OTel resource's
`service.name`. They must be the same string — Datadog joins a log to its trace on
service, so a mismatch correlates with nothing, and a shared name across hosts (the old
hardcoded `carditrack`) collapses every app into one service on the Service facet.

### Release version on telemetry (`DeploymentInfo`)

Logs carry a `Version` property **and a `version:<semver>` Datadog tag**, and the OTel
resource carries `service.version`, so telemetry can be attributed to a release. The tag
is what Datadog's reserved **Version** facet and its release comparison actually read —
the log property alone is an ordinary attribute and leaves the facet empty, which is why
both are sent. The value is the deploy's
semver tag: CI computes it (`v1.2.3`), tags the image with it as-is, and passes it —
**without the leading `v`**, which MSBuild will not accept in a `Version` — as the
Dockerfile's `VERSION` build arg for `-p:Version=` to stamp into the assembly.
`DeploymentInfo` reads it back, trimming the `+<sha>` the SDK appends. Net effect: an
image tagged `v1.2.3` reports `1.2.3`, so the two differ by that one character and
nothing else.

Builds outside the release pipeline report `0.0.0-local` (the host projects' default
`<Version>`) rather than posing as a release. The plaintext `DEPLOY_VERSION` env var
overrides the baked-in value for out-of-band images; normal deploys leave it unset.

### Environment on telemetry (`DeploymentInfo.EnvironmentName`)

Logs carry an `env:<name>` tag and spans carry `deployment.environment.name` (plus the
older `deployment.environment`, because OTLP intakes are mid-migration between the two
keys). With service and version this is the `env`/`service`/`version` triple backends
group telemetry by.

The environment is the one piece that **cannot be baked into the image**: dev and prod run
the *same* image, promoted by tag, so it has to arrive at runtime. It comes from
`ASPNETCORE_ENVIRONMENT`, which Terraform sets per environment as `title(var.environment)`
— `Dev` / `Prod`, deliberately not .NET's `Development` / `Production`, so deployed hosts
all run production-like config. `DeploymentInfo` lowercases it (`env:dev`, `env:prod`);
tags are case-sensitive, so `Dev` and `dev` would otherwise be two environments.
`DEPLOY_ENVIRONMENT` overrides it when telemetry should be labelled differently from the
name that selects appsettings files.

The env vars are read **raw, not through `IHostEnvironment`**, which substitutes
`Production` whenever nothing is set. With neither set, the environment resolves to null:
the `env` tag is omitted and startup logs a Warning naming both variables. A missing
environment is visibly missing; an invented `prod` is a false alarm.

## Project Structure

> **Target structure** — the tree below is the planned layout, not a mirror of the current code. Today's `Controllers/` holds `Alerts`, `Auth`, `Onboarding`, `CardiMembers`, `Dashboard`, `Devices`, `Reports`, `Chat`, and `Insights` controllers (30 endpoints total), all deriving from `BaseApiController`; the `Webhooks/` folder (Google Health API, Garmin, Stripe) arrives with the AI-pipeline rollout ([llm_design.md](../../llm_design.md)).
>
> **Routing note:** `BaseApiController` carries the route template `api/[controller]` (plus `[ApiController]`, JSON `Produces`, and the standard `ApiResponse<T>`/`ErrorResponse` envelope helpers). Eight of the nine controllers override it with explicit **`/api/v1/*`** routes; only `OnboardingController` still serves **`/api/Onboarding/*`**-style routes. API versioning is registered (default `1.0`, assumed when unspecified); moving Onboarding onto the versioned template is the remaining spec/code alignment task.

```
CardiTrack.API/
├── Controllers/
│   ├── CardiMembersController.cs
│   ├── DashboardController.cs
│   ├── AlertsController.cs
│   ├── DevicesController.cs
│   ├── FamilyController.cs
│   ├── NotificationsController.cs
│   ├── SubscriptionsController.cs
│   ├── ReportsController.cs
│   └── Webhooks/
│       ├── HealthWebhookController.cs      # Google Health API webhooks — verifies auth, forwards to Event Hubs
│       ├── GarminWebhookController.cs
│       └── StripeWebhookController.cs
├── DTOs/
│   ├── Requests/
│   └── Responses/
├── Middleware/
│   ├── ErrorHandlingMiddleware.cs
│   ├── AuditLoggingMiddleware.cs
│   └── HipaaComplianceMiddleware.cs
├── Extensions/
│   ├── ServiceCollectionExtensions.cs
│   ├── Auth0Extensions.cs
│   ├── SwaggerExtensions.cs
│   └── SerilogExtensions.cs
├── Infrastructure/
│   └── HealthChecks/
├── Program.cs
└── appsettings.json
```

## Authentication

The API accepts Auth0-issued Bearer tokens only:

```
Authorization: Bearer <access_token>
```

Token policy, JWT claims, and the Universal Login flow are specified in [auth.md](../../execution/backend/api/auth.md). There are no local register/login endpoints.

## Error Handling

The standard error envelope, status-code table, and per-endpoint error codes are defined in the [API spec readme](../../execution/backend/api/readme.md).

## Rate Limiting

Rate limiting is **IP-based only** (AspNetCoreRateLimit, in-memory counters via
`AddInMemoryRateLimiting`) — there is no per-user or per-subscription-tier awareness. The rules
live in the `IpRateLimiting` section of `appsettings.json`:

- **Global**: 100 requests/minute **and** 1,000 requests/hour per IP (all endpoints)
- **`/api/v1/auth/resend-verification`**: 5 requests/hour per IP

Throttled requests get `429` with AspNetCoreRateLimit's standard headers:

```
X-Rate-Limit-Limit: 1m
X-Rate-Limit-Remaining: 95
X-Rate-Limit-Reset: 2026-08-07T12:01:00.0000000Z
```

> Tier-aware limits (e.g. higher quotas for API-access plans) are **planned**, not implemented.

## HIPAA Compliance

- PHI access is audit-logged via the opt-in `AuditHealthDataAccess` attribute, applied controller-wide on the six health-data controllers (CardiMembers, Dashboard, Devices, Insights, Chat, Reports); entries record user ID, CardiMember ID, action, timestamp, IP, and user agent. Unannotated endpoints (Auth, Onboarding) are **not** audited — onboarding's CardiMember creation is a known audit gap. **Retention policy is 6 years**; the deployed infrastructure currently retains 30 days (dev) / 90 days (prod) via tfvars — extending it to the policy horizon is tracked follow-up infra work
- TLS 1.2+ in transit; Cloud SQL encryption at rest (Google-managed keys); field-level AES-256-GCM encryption for OAuth tokens and medical notes
- See [infrastructure.md](../../infrastructure.md) for encryption and key management details

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=carditrack;Username=postgres;Password=postgres",
    "Redis": "localhost:6379"
  },
  "Auth0": {
    "Domain": "carditrack.auth0.com",
    "Audience": "https://api.carditrack.com"
  },
  "Cors": {
    "AllowedOrigins": [ "https://localhost:7002", "http://localhost:3000" ]
  },
  "DeviceProviders": [
    {
      "Provider": "Fitbit",
      "ClientId": "<Google Cloud OAuth client id>",
      "ClientSecret": "<Google Cloud OAuth client secret>",
      "AuthorizationUrl": "https://accounts.google.com/o/oauth2/v2/auth",
      "TokenUrl": "https://oauth2.googleapis.com/token",
      "ApiBaseUrl": "https://health.googleapis.com",
      "Scopes": [
        "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly",
        "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly",
        "https://www.googleapis.com/auth/googlehealth.sleep.readonly",
        "https://www.googleapis.com/auth/googlehealth.settings.readonly"
      ],
      "RedirectUri": "https://api.carditrack.com/api/v1/oauth/redirect/fitbit",
      "AdditionalAuthorizationParams": {
        "access_type": "offline",
        "prompt": "consent"
      },
      "TokenLifetimeHours": 1
    }
  ],
  "AI": {
    "GeneralProvider": "Gemini",
    "MedicalProvider": "MedGemma",
    "Providers": [
      { "Name": "MedGemma", "BaseUrl": "http://localhost:11434", "Model": "medgemma", "TimeoutSeconds": 120 },
      { "Name": "Gemini", "BaseUrl": "https://generativelanguage.googleapis.com", "Model": "gemini-2.0-flash", "ApiKey": "" }
    ]
  }
}
```

Secrets are supplied via environment variables backed by **GCP Secret Manager** in all deployed environments — never committed. (See `api_secret_env_vars` in `infrastructure/main.tf` for the full env-var → secret mapping.)

> The Fitbit provider runs on the **Google Health API** (Google OAuth endpoints, `googlehealth.*` scope URIs, ~1-hour access tokens). `RedirectUri` is the provider-facing **https bounce endpoint** — Google web OAuth clients cannot redirect to a custom scheme, so `GET /api/v1/oauth/redirect/fitbit` 302s back into the app deep link. `AdditionalAuthorizationParams` carries Google's `access_type=offline` (required for a refresh token) and `prompt=consent`. Client id/secret come from Secret Manager (`devices-fitbit-client-id` / `devices-fitbit-client-secret`). Event ingestion config for the AI pipeline arrives with its rollout ([llm_design.md](../../llm_design.md)).

### Device providers — positional-index contract

`DeviceProviders` in appsettings is a JSON **array**, and deployment injects the Fitbit
credentials positionally (`DeviceProviders__0__ClientId` / `DeviceProviders__0__ClientSecret`
env vars in `infrastructure/main.tf`). **Element 0 must therefore be the Fitbit provider** —
`AddFitbitProvider()` post-configures the list and **throws at startup** if the first element
is anything else, rather than silently binding Google credentials to the wrong provider.
The `Garmin`, `Withings`, `Oura`, and `Whoop` entries that follow are **config-only stubs**:
no API client or sync service is registered for them yet.

### AI providers

`AddAiServices()` wires two AI systems, and only one of them is swappable:

- **`AI:Public`** — reports and chat. `AI__Public__Kind` picks the provider: `Gemini`
  (`GeminiClient`, hosted Google Generative Language API) or `Anthropic` (`AnthropicAiClient`,
  Messages API via the official SDK). The key comes from the `gemini-api-key` secret as
  `AI__Public__ApiKey`; `AI__Public__BaseUrl` is optional and defaults per kind.
- **`AI:Private`** — health insights. **Always MedGemma** (`MedGemmaClient`, an Ollama-served
  model on its own Cloud Run service, reachable only by IAM-authorised callers). There is no kind here: the provider is
  fixed in code, so no environment variable can send health data off-estate. Base URL comes
  from the `medgemma-service-url` secret as `AI__Private__BaseUrl` — locally it defaults to
  `http://localhost:11434`.

Both resolve as keyed `IExternalAiClient` services ("GeneralProvider" / "MedicalProvider")
behind `IGenerativeAiService`, `IMedicalAiService`, `IHealthInsightService`, and
`IReportGenerationService`. Configuration is validated at startup — an unknown kind, a missing
model or key, a non-positive timeout, or a URL without an http(s) scheme fails the host with a
message naming the environment variable to fix.

### Caching

`AddCachingServices()` registers a distributed cache: **Redis** when
`ConnectionStrings:Redis` is set (StackExchange, instance prefix `CardiTrack_`), otherwise an
**in-memory fallback** (`AddDistributedMemoryCache`). The cache is not optional in practice —
it holds the OAuth PKCE state during device linking, and the authorize and callback legs of
that flow can land on different Cloud Run instances.

Dev runs a **Memorystore for Redis** instance (`enable_redis` in Terraform, see
[infrastructure.md](../../infrastructure.md#caching)) with AUTH and in-transit encryption on;
Terraform writes the connection string and the instance's CA bundle to Secret Manager, and
Cloud Run injects them as `ConnectionStrings__Redis` and `Redis__CaCertificate`. Because the
instance is reached on a private IP that its certificate does not carry, the default hostname
check cannot pass; `RedisCertificateValidation` pins the per-instance CA instead.

**Prod has no instance yet** (`enable_redis = false`). Note that `appsettings.json` still
defaults `ConnectionStrings:Redis` to `localhost:6379`, so an environment without the env var
does not reach the in-memory fallback — it registers Redis against its own loopback and every
cache write times out.

### Identity & user context

- JWT validation happens first; then `UserContextMiddleware` populates a scoped
  `IUserContext` from token claims (Auth0 user id, email, the tenant Action's namespaced
  `email_verified` claim, locale from `Accept-Language`) and enriches it with the database
  identity (`UserId`, `OrganizationId`, `Role`) when the user record exists — during
  onboarding, before the user row is created, `UserId` stays `Guid.Empty`.
- `Users.Auth0UserId` has a **unique filtered index** (`"Auth0UserId" <> ''`), making the
  Auth0-identity → user lookup safe and onboarding retries idempotent.
- `POST /api/Onboarding/setup` creates the organization, trial subscription, and user
  **atomically in one call** — preferred over the legacy separate `organization`/`user`
  endpoints, which can strand an orphaned organization if the client dies between calls
  (the Worker's cleanup job sweeps those up).

## Running Locally

```bash
# Navigate to API project
cd src/Presentation/CardiTrack.API

# Restore dependencies
dotnet restore

# Update database
dotnet ef database update --project ../../Infrastructure/CardiTrack.Infrastructure

# Run API
dotnet run

# API will be available at (launchSettings.json):
# https://localhost:7130
# http://localhost:5230
```

## Swagger Documentation

Swagger is registered in **non-production environments only** (`!IsProduction()`). When running locally, access Swagger UI at:
```
https://localhost:7130/swagger
```

## Health Checks

#### GET /health

Requires the `X-Health-Token` header (value from the `Health:Token` secret); requests without it get `401`. The endpoint uses the default ASP.NET Core health-check writer, so it returns a plain-text status:

```
Healthy
```

> Named sub-checks (database, redis, Google Health API reachability) are planned but not yet registered — `Program.cs` currently calls `AddHealthChecks()` with no checks added.

## Testing

```bash
# Run unit tests
dotnet test tests/CardiTrack.UnitTests

# Run integration tests
dotnet test tests/CardiTrack.IntegrationTests

# Run all tests
dotnet test
```

## Deployment

The API ships as two container images, both multi-stage but on different runtime bases:

- **`Dockerfile`** — the API service itself, deployed to **Cloud Run** (binds the base image's `ASPNETCORE_HTTP_PORTS=8080`). Runtime is `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`, non-root UID 1654 — the `-extra` variant carries ICU and tzdata, which the plain chiseled base lacks.
- **`Dockerfile.migrate`** — an EF Core **migrator image** (`dotnet ef database update` entrypoint) deployed as a **Cloud Run Job**; it runs against the private Cloud SQL instance via the Auth Proxy socket and exits after applying pending migrations. It builds and runs directly from `mcr.microsoft.com/dotnet/sdk:10.0` (needs `dotnet ef` installed at runtime), not a chiseled image.

See the [Infrastructure Guide](../../infrastructure.md) for the full deployment pipeline.

## Related Documentation

- [Canonical API spec](../../execution/backend/api/readme.md)
- [Release matrix](../../release_matrix.md)
- [Infrastructure Guide](../../infrastructure.md)
- [LLM / AI pipeline design](../../llm_design.md)

## Support

For API support, contact: api-support@carditrack.com

---

**Last Updated:** August 7, 2026
