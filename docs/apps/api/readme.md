# CardiTrack API — Application Overview

## Overview

The CardiTrack API is a RESTful ASP.NET Core 10 Web API that serves as the backend for the CardiTrack platform. It handles authentication (Auth0 JWT validation), device integrations, health data processing, alert management, and family member coordination.

> **Endpoint documentation lives in the canonical API spec: [/docs/execution/backend/api/](../../execution/backend/api/readme.md).** This document covers only the application itself — stack, structure, configuration, and local development. All routes are versioned under `/api/v1/`.

## Technology Stack

- **.NET 10**: Core framework
- **ASP.NET Core 10**: Web API framework
- **Entity Framework Core**: ORM for Azure SQL (transactional system of record — see [storage boundary](../../infrastructure.md#storage-boundary))
- **Auth0**: Authentication — the API validates Auth0-issued JWTs; it does not issue tokens or store credentials (see [auth.md](../../execution/backend/api/auth.md))
- **Swagger/OpenAPI**: API documentation
- **SignalR**: Real-time notifications to the web dashboard
- **Serilog**: Structured logging (console + rolling file + Seq; APM shipping when configured)
- **OpenTelemetry**: Tracing exported to the configured APM backend over OTLP

### APM shipping (`CardiTrack.Observability`)

The APM backend is switchable via the `Apm` config section, consumed by both the API and Web
through `CardiTrack.Observability` (`AddApmShipping` for Serilog, `AddApmTracing` for OTel):

```json
"Apm": {
  "Engine": "BetterStack",            // provider name; see ApmProviderRegistry
  "Data": {                           // connection details for the selected engine
    "IngestUrl": "",
    "IngestToken": "",
    "Extra": {}                       // optional provider-specific keys (region, dataset, ...)
  },
  "MinimumLogLevel": "Warning",
  "TracesSampleRatio": 0.2
}
```

Shipping is **disabled until `Data.IngestUrl` and `Data.IngestToken` are set** (for Better Stack:
the per-source ingesting host and source token from Better Stack → Sources). In deployed
environments these arrive as Secret Manager-backed env vars (`Apm__Data__IngestUrl` /
`Apm__Data__IngestToken` from the `carditrack-<env>-apm-ingest-*` secrets); `REPLACE_ME`
placeholders count as unset. Provisioning: [APM setup runbook](../../technical/apm_setup_runbook.md)
+ `scripts/set-apm-secrets.sh`. An unknown `Engine` fails startup loudly. To support a new
backend, implement `IApmProvider` and register it in `ApmProviderRegistry` — nothing changes
in the apps.

Free-tier prudence (enforced engine-independently in `ApmExtensions`):

- Only `MinimumLogLevel` and above (default `Warning`) is shipped; full detail stays in console/file/Seq.
- Traces are head-sampled via `TracesSampleRatio` (default `0.2`); `/health` requests are never traced.
- Metrics are deliberately not exported.

## Project Structure

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
│       ├── FitbitWebhookController.cs      # validates signature, forwards to Event Hubs
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

- **Anonymous** (webhooks, health): 10 requests/minute per IP
- **Authenticated**: 100 requests/minute
- **Guardian Plus / API-access plans**: 1000 requests/minute

Headers returned:
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 95
X-RateLimit-Reset: 1704844800
```

## HIPAA Compliance

- All PHI access is audit-logged (user ID, CardiMember ID, action, timestamp, IP, user agent) with **6-year retention**
- TLS 1.2+ in transit; Azure SQL TDE at rest; field-level AES-256-GCM encryption for OAuth tokens and medical notes
- See [infrastructure.md](../../infrastructure.md) for encryption and key management details

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=...",
    "Redis": "..."
  },
  "Auth0": {
    "Domain": "carditrack.auth0.com",
    "Audience": "https://api.carditrack.com"
  },
  "Fitbit": {
    "ClientId": "...",
    "ClientSecret": "...",
    "CallbackUrl": "https://api.carditrack.com/api/v1/oauth/callback/fitbit"
  },
  "EventHubs": {
    "ConnectionString": "...",
    "HubName": "fitbit-raw"
  },
  "Twilio": {
    "AccountSid": "...",
    "AuthToken": "...",
    "FromNumber": "+1234567890"
  },
  "ApplicationInsights": {
    "ConnectionString": "..."
  }
}
```

Secrets are supplied via environment variables or Azure Key Vault in all deployed environments — never committed.

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

# API will be available at:
# https://localhost:7001
# http://localhost:5001
```

## Swagger Documentation

When running locally, access Swagger UI at:
```
https://localhost:7001/swagger
```

## Health Checks

#### GET /health

```json
{
  "status": "Healthy",
  "checks": {
    "database": "Healthy",
    "redis": "Healthy",
    "fitbit_api": "Healthy"
  }
}
```

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

See the [Infrastructure Guide](../../infrastructure.md) for deployment instructions.

## Related Documentation

- [Canonical API spec](../../execution/backend/api/readme.md)
- [Release matrix](../../release_matrix.md)
- [Infrastructure Guide](../../infrastructure.md)
- [LLM / AI pipeline design](../../llm_design.md)

## Support

For API support, contact: api-support@carditrack.com
