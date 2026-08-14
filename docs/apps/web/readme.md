# CardiTrack Web Dashboard Documentation

## Overview

`CardiTrack.Web` is a .NET 10 **Blazor Web App** (static server-side rendering with per-component `InteractiveServer` islands) that will become the family/caregiver dashboard. Today it is deployed to **Cloud Run** but is functionally an early shell: the template pages are still in place, there is no login, and the one piece of product UI is the health-data disclosure banner. This document describes **what exists now** first, and keeps the product vision in a clearly separated [Planned](#planned) section.

## Current State

### Technology stack

- **.NET 10 / Blazor Web App**: static SSR by default; individual components opt into `InteractiveServer` render mode (there is no globally interactive circuit)
- **Bootstrap 5**: bundled under `wwwroot/lib/bootstrap`
- **Entity Framework Core (Npgsql)**: the Web app talks to Cloud SQL **PostgreSQL directly** (see below)
- **Serilog**: console logging plus APM shipping via `CardiTrack.Observability` (`AddApmShipping` / `AddApmTracing` — same `Apm` config contract as the API)

### Project structure (actual)

```
src/Presentation/CardiTrack.Web/
├── Components/
│   ├── App.razor
│   ├── Routes.razor
│   ├── _Imports.razor
│   ├── Layout/
│   │   ├── MainLayout.razor            # Template layout + disclosure banner mount
│   │   ├── NavMenu.razor               # Template sidebar (Home/Counter/Weather)
│   │   └── ReconnectModal.razor        # Blazor reconnection UI
│   ├── Pages/
│   │   ├── Home.razor                  # Template "Hello, world!"
│   │   ├── Counter.razor               # Template leftover
│   │   ├── Weather.razor               # Template leftover
│   │   ├── Privacy.razor               # Public privacy policy (real content)
│   │   ├── Error.razor
│   │   └── NotFound.razor
│   └── Shared/
│       ├── HealthDataDisclosureBanner.razor
│       └── HealthDataDisclosureBanner.razor.css
├── wwwroot/
├── Program.cs
├── appsettings.json
├── Dockerfile
└── CardiTrack.Web.csproj
```

### Pages

| Route | Page | Status |
|---|---|---|
| `/` | Home | Template "Hello, world!" placeholder |
| `/counter` | Counter | Template leftover |
| `/weather` | Weather | Template leftover |
| `/privacy` | Privacy | **Real prose, placeholder policy** — covers health-data collection, use, retention, and control (contact `cloudoperations@codesistance.com`), but has no versioning, effective date, or GDPR/data-subject sections; needs legal review before Auth0 login ships |
| `/not-found` | NotFound | Served via `UseStatusCodePagesWithReExecute` |
| `/Error` | Error | Production exception handler target |

### Health-data disclosure banner (PR #9)

`Components/Shared/HealthDataDisclosureBanner.razor` is the app's first product component — a Google Health API / health-data disclosure notice:

- **Mount point**: `MainLayout.razor`, directly above `@Body`, as an interactive island — `@rendermode="new InteractiveServerRenderMode(prerender: false)"` inside the otherwise static layout.
- **Audience**: authenticated users only. It reads the cascading `Task<AuthenticationState>`; unauthenticated (or absent) auth state means it renders nothing. Since no login is wired up yet, the banner is **currently inert in every environment** — it lights up automatically once Auth0 web login lands.
- **Persistence**: per-user, server-side, via `IUserService.HasDismissedHealthDataDisclosureAsync` / `DismissHealthDataDisclosureAsync`, backed by `User.HealthDataDisclosureDismissedDate` — the dismissal follows the user across devices and sessions. The banner hides **only after the dismissal has actually persisted**; if persistence fails (e.g. unknown user), the disclosure keeps showing.
- **Content**: states that CardiTrack collects health and fitness data for anomaly alerts, daily digests, and trend monitoring, with a "Learn more" link to `/privacy`.
- **Tests**: bUnit component tests in `tests/CardiTrack.UnitTests/Web/HealthDataDisclosureBannerTests.cs`.

### Application startup (actual `Program.cs`)

In order, `Program.cs` wires:

1. **Serilog** — console sink always, `AddApmShipping` when the `Apm` engine is configured; `Npgsql.EnableLegacyTimestampBehavior` disabled (UTC everywhere). Enriched with the release version (`DeploymentInfo.Version`) as the `Version` property.
2. **APM tracing** — `AddApmTracing(ApmServiceNames.Web)` (no-op until `Apm__Engine` + `Apm__Data` are set); reports the same release as OTel's `service.version`. The app names itself `web` to the backend — the same constant the log sink gets, so logs and traces share one service.
3. **Razor components** — `AddRazorComponents().AddInteractiveServerComponents()`.
4. **Auth state** — `AddCascadingAuthenticationState()` so components can read the principal. **No authentication scheme is registered yet**, so the cascaded principal is always unauthenticated.
5. **Database + repositories** — `CardiTrackDbContext` on Npgsql plus the full repository set (`IOrganizationRepository` … `IPatternBaselineRepository`, and the later additions `IGranularMetricRepository`, `IDigestRepository`, `IRealtimeAssessmentRepository`, `IMemberQuestionnaireRepository`, `IEnvironmentalReadingRepository`, `INotificationRepository`, `INotificationMuteRepository`, plus `AddPushRepositories()`), `IUnitOfWork`, and `IUserService`. **Architecturally notable: the Web app talks to PostgreSQL directly** (it needs `IUserService` for the banner's per-user dismissal, and `UnitOfWork` requires every repository) rather than going through the API.
6. **HTTP client** — a named `CardiTrackApiClient` `HttpClient` whose base address comes from the `Api:BaseUrl` config key (for future API-backed features).
7. **Data protection** — when `DataProtection:KeysPath` is set, the key ring persists to that directory; deployed, this is a **GCS bucket mounted as a Cloud Run volume**, so antiforgery tokens survive container recycling and validate across instances. Unset locally (default container-local store).

Middleware pipeline: exception handler + HSTS outside Development, `UseStatusCodePagesWithReExecute("/not-found")`, HTTPS redirection, Serilog request logging, antiforgery, `MapStaticAssets`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`.

### Configuration (actual `appsettings.json` shape)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=carditrack;Username=postgres;Password=postgres"
  },
  "Api": {
    "BaseUrl": "https://localhost:7001"
  },
  "Serilog": {
    "MinimumLevel": { "Default": "Warning", "Override": { "Microsoft": "Warning" } }
  },
  "Apm": {
    "Engine": "",
    "Data": { "IngestUrl": "", "IngestToken": "" },
    "MinimumLogLevel": "Warning",
    "TracesSampleRatio": 1.0
  },
  "AllowedHosts": "*"
}
```

Deployed, `Apm__Engine` / `Apm__MetricsEnabled` / `Apm__TracesSampleRatio` / `Serilog__MinimumLevel__Default` arrive as plaintext env vars and `Apm__Data` from Secret Manager (`carditrack-<env>-apm-data`) — see the [API readme's APM section](../api/readme.md#apm-shipping-carditrackobservability) for the shared contract. The last two come from the per-service `traces_sample_ratio` / `log_minimum_level` tfvars (`web` attribute), so Web's volume can be tuned without touching API or Worker.

### Running locally

```bash
cd src/Presentation/CardiTrack.Web
dotnet run

# From launchSettings.json:
# https://localhost:7177
# http://localhost:5026
```

Prerequisites: .NET 10 SDK and a local **PostgreSQL 17** with the CardiTrack database (the app opens a DB connection for the banner's user lookup). Cloud SQL remains PostgreSQL 16.

### Docker & deployment

A real `Dockerfile` exists (multi-stage: `sdk:10.0` build → `aspnet:10.0-noble-chiseled-extra` runtime, non-root UID 1654, same pattern as the API). The `-extra` variant carries ICU and tzdata, which plain chiseled lacks. The app deploys as a **Cloud Run service** (`carditrack-<env>-web`); Terraform supplies the APM env vars and mounts the data-protection GCS volume.

Web runs as its **own runtime service account** (`carditrack-<env>-web`, `infrastructure/deployments/service_accounts.tf`) with only three grants: Cloud SQL client, secret accessor on `carditrack-<env>-apm-data`, and object access on the data-protection keys bucket — deliberately no Auth0 or encryption-key access.

Web's **managed TLS certificate is reissuable** via the `web_cert_generation` suffix with `create_before_destroy` (`infrastructure/deployments/load_balancer.tf`): bumping `web_cert_generation` renames the certificate resource, and the new one is created and attached before the old is removed — the whole procedure for forcing a fresh cert after an expiry.

### Testing

`tests/CardiTrack.UnitTests` already includes **bUnit** as a dependency; Web component tests live under `tests/CardiTrack.UnitTests/Web/`.

```bash
dotnet test tests/CardiTrack.UnitTests
```

## Planned

None of the following exists in the Web app yet — do not read the sections above as implying it:

- **Dashboard UI** — real-time health metrics, trend charts, device/sync status per CardiMember
- **Alert management UI** — severity levels, acknowledgment, history, filtering
- **Member & family management** — multi-member overview, member profiles, role-based access, organization management
- **Auth0 web login** — cookie + OIDC Universal Login flow; the cascading auth state and the disclosure banner are already wired to take advantage of it
- **Real-time updates** — server-pushed updates (e.g. SignalR or Pub/Sub-driven) for live metrics and alerts

## Related Documentation

- [API Documentation](../api/readme.md)
- [Mobile App Documentation](../mobile/readme.md)
- [Infrastructure Guide](../../infrastructure.md)
- [Authentication Setup](../../technical/auth0_integration.md)

## Support

For web dashboard issues, contact: web-support@carditrack.com

---

**Last Updated:** August 14, 2026
