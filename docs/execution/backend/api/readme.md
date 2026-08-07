# CardiTrack Backend API Documentation

This directory documents the REST API supporting the CardiTrack mobile and web applications, derived from the [Mobile User Stories](../../ui/mobile/user_stories.md). It covers both the **implemented** surface (verified against `src/Presentation/CardiTrack.API`) and **planned** endpoints kept as design intent — each file states which is which.

## Base URL

```
https://api.carditrack.com
```

Most routes are prefixed `/api/v1/...`. Two exceptions:

- **Onboarding** routes use the controller-name convention and live at `/api/Onboarding/...` (no `v1` segment).
- The **health check** is at `/health` (no `/api` prefix).

## Authentication

Endpoints require a JWT Bearer token issued by **Auth0 Universal Login** (Authorization Code + PKCE). The API validates tokens; it does not issue them. Token lifetime is validated with **zero clock skew** (`ClockSkew = TimeSpan.Zero`) — an expired token is rejected immediately, with no grace window.

```
Authorization: Bearer <access_token>
```

**Anonymous exceptions:**

| Endpoint | Why anonymous |
|----------|---------------|
| `POST /api/v1/auth/resend-verification` | Caller cannot log in until verified; rate-limited 5/hour/IP, always returns 200 (no user enumeration) |
| `GET /api/v1/oauth/redirect/{provider}` | Provider-facing OAuth bounce; scoped by the single-use state token, only redirects into the `carditrack://` app scheme |
| `GET /health` | Health probe; requires the `X-Health-Token` header instead of a JWT (wrong/missing token → 401) |

**Token policy** (see [auth.md](auth.md)): access tokens live 15–60 minutes; rotating refresh tokens have a 30-day absolute lifetime; web sessions idle out after ~15 minutes; on mobile the refresh token sits behind a biometric gate in secure storage.

> **JWT valid but no local user row:** most controllers return **403** with a "please sign in again" message when the token is valid but onboarding hasn't created the local `Users` row yet. `ReportsController` inconsistently returns **401** in the same situation — a known drift, tracked for alignment.

## Versioning

`/api/v1/` routes carry the version explicitly; a default API version of 1.0 is assumed when unspecified (which is how the unversioned `/api/Onboarding/*` routes resolve). Breaking changes will increment the version.

## Response Envelope

All 2xx JSON responses are wrapped in a standard envelope (`ApiResponse<T>`):

```json
{
  "success": true,
  "message": "Here you go!",
  "data": { },
  "timestamp": "2026-08-07T10:00:00Z"
}
```

The only unwrapped success responses are the `302` OAuth bounce redirect and the report **download** file stream.

## Standard Error Format

Errors return an `ErrorResponse` body. There is **no machine-readable `code` field** — clients branch on the HTTP status; `message` is human-readable copy.

```json
{
  "success": false,
  "message": "Some details need a second look — please check them and try again.",
  "errors": [
    { "field": "Name", "message": "Name is required" }
  ],
  "traceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "timestamp": "2026-08-07T10:00:00Z"
}
```

`errors` is populated for validation failures (field-level FluentValidation messages) and is empty otherwise. `traceId` is set by the exception-handling middleware for correlating with server logs.

| HTTP Status | When Used |
|-------------|-----------|
| 200 | Success |
| 201 | Resource created (onboarding resources, completed device connection) |
| 202 | Accepted for async processing (report generation) |
| 302 | OAuth bounce redirect into the mobile app deep link |
| 400 | Validation error / bad request (including unsupported OAuth provider, bad state token) |
| 401 | Missing or invalid token (and the ReportsController drift noted above) |
| 403 | Authenticated but not authorized (including "no local user row yet") |
| 404 | Resource not found or not accessible to the caller |
| 409 | Conflict — currently only "report not ready yet" on download |
| 429 | IP rate limit exceeded |
| 500 | Internal server error |
| 502 | Upstream provider failure (OAuth code exchange rejected by the wearable provider) |

## Data Conventions

- **Enums serialize as integers.** The API uses default `System.Text.Json` settings with no string-enum converter, so enum-typed fields (`gender`, `relationship`, `role`, `format`, `status` on reports, `severity` on alert insights, subscription `tier`/`status`, …) are **integers on the wire**. Fields documented as lowercase strings (dashboard `healthStatus`, device `status`, metric `status`) are explicit string properties mapped in code, not serialized enums.
- **IDs are raw GUIDs** (`"3fa85f64-5717-4562-b3fc-2c963f66afa6"`). There are no `cm_`/`dev_`/`usr_` style prefixes. The one exception: **report IDs** are GUIDs in compact `"N"` format (32 hex chars, no dashes).
- Dates are ISO 8601; `DateOnly` fields serialize as `"2026-08-07"`.

## Rate Limiting

IP-based (AspNetCoreRateLimit, in-memory), returning **429** when exceeded:

| Scope | Limit |
|-------|-------|
| All endpoints | 100 requests / minute / IP |
| All endpoints | 1 000 requests / hour / IP |
| `POST /api/v1/auth/resend-verification` | 5 requests / hour / IP |

## CORS

Cross-origin requests are restricted to a configured **origin allow-list** (`Cors:AllowedOrigins`); allowed origins get any header/method with credentials. Non-listed origins are refused CORS headers.

## Implemented Endpoints (August 2026)

The full implemented surface is 18 endpoints across 8 controllers:

| Method + Route | Purpose | Doc |
|----------------|---------|-----|
| `POST /api/v1/auth/resend-verification` | Resend Auth0 verification email (anonymous) | [auth.md](auth.md) |
| `POST /api/Onboarding/setup` | Atomic org + trial subscription + user creation | [auth.md](auth.md), [subscriptions.md](subscriptions.md) |
| `POST /api/Onboarding/organization` | Create organization | [cardimembers.md](cardimembers.md) |
| `POST /api/Onboarding/user` | Create user linked to Auth0 | [auth.md](auth.md) |
| `POST /api/Onboarding/cardimember` | Create CardiMember | [cardimembers.md](cardimembers.md) |
| `GET /api/Onboarding/status` | Onboarding progress for current user | — |
| `GET /api/Onboarding/cardimembers` | List org's CardiMembers | [cardimembers.md](cardimembers.md) |
| `GET /api/v1/cardimembers/{id}/dashboard` | Composed per-member dashboard | [health-data.md](health-data.md) |
| `GET /api/v1/cardimembers/{id}/devices` | List wearable connections | [devices.md](devices.md) |
| `POST /api/v1/cardimembers/{id}/devices` | Initiate PKCE OAuth connection | [devices.md](devices.md) |
| `GET /api/v1/oauth/redirect/{provider}` | Anonymous OAuth bounce (302) | [devices.md](devices.md) |
| `POST /api/v1/oauth/callback/{provider}` | Complete OAuth, store connection (201) | [devices.md](devices.md) |
| `POST /api/v1/chat` | AI chat with recent health data as context | — |
| `GET /api/v1/insights/alerts/{alertId}` | MedGemma analysis of an alert | [alerts.md](alerts.md) |
| `GET /api/v1/insights/members/{id}/baseline` | MedGemma narrative baseline analysis | [health-data.md](health-data.md) |
| `POST /api/v1/reports` | Queue async report generation (202) | [reports.md](reports.md) |
| `GET /api/v1/reports/{reportId}` | Poll report status | [reports.md](reports.md) |
| `GET /api/v1/reports/{reportId}/download` | Download completed report | [reports.md](reports.md) |

Plus `GET /health` — anonymous liveness probe gated by the `X-Health-Token` header.

## MVP Priority Legend

| Priority | Meaning |
|----------|---------|
| **P0** | Must Have — MVP launch blocker |
| **P1** | Should Have — MVP launch goal |
| **P2** | Nice to Have — post-launch sprint |
| **Future** | Post-MVP roadmap |

## API Domains

| File | Domain | Status | Key User Stories |
|------|--------|--------|-----------------|
| [auth.md](auth.md) | Authentication | **Implemented** (Auth0-hosted; one API endpoint) | 1.1, 10.2 |
| [cardimembers.md](cardimembers.md) | CardiMember Management | **Planned** — onboarding endpoints cover create/list today | 1.2, 7.1, 7.2, 7.3 |
| [devices.md](devices.md) | Device Management | **Implemented** (connect/list/OAuth); manage endpoints planned | 1.3, 6.2 |
| [health-data.md](health-data.md) | Health Data & Dashboard | **Partially implemented** (per-member dashboard, AI baseline) | 2.1, 2.2, 2.3, 5.2, 10.1 |
| [alerts.md](alerts.md) | Alerts & Notification Preferences | **Planned** — alert AI insight endpoint exists | 3.1, 3.2, 3.3, 11.1–11.3 |
| [family.md](family.md) | Family Collaboration | **Planned** | 4.1, 4.2, 8.3 |
| [notifications.md](notifications.md) | Push Notifications | **Planned** | 3.2, 5.1 |
| [subscriptions.md](subscriptions.md) | Subscription Management | **Planned** — trial auto-created at onboarding | 6.1 |
| [reports.md](reports.md) | Reports & Exports | **Implemented** (LLM text output; PDF/CSV/FHIR planned) | 2.3, 9.2 |
| — | Onboarding | **Implemented** (`/api/Onboarding/*`) | 1.1, 1.2 |
| — | Dashboard | **Implemented** (`GET /api/v1/cardimembers/{id}/dashboard`) | 2.1 |
| — | AI Chat | **Implemented** (`POST /api/v1/chat`) | — |
| — | AI Insights | **Implemented** (`/api/v1/insights/*`) | 3.1 |
| — | Health check | **Implemented** (`GET /health`) | — |

## Related Documentation

- [Mobile User Stories](../../ui/mobile/user_stories.md)
- [Web User Stories](../../ui/web/user_stories.md)
- [Entity Summary](../../../technical/entity_summary.md)
- [Auth0 Integration](../../../technical/auth0_integration.md)
- [User Onboarding Process](../../../technical/user_onboarding_process.md)
- [OAuth Client Inventory](../../../technical/oauth_clients.md)

---

**Document Version:** 2.0
**Last Updated:** August 7, 2026
**Owner:** Backend Engineering Team
