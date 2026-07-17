# CardiTrack Backend API Documentation

This directory documents the REST API endpoints required to support the CardiTrack mobile and web applications, derived from the [Mobile User Stories](../../ui/mobile/user_stories.md).

## Base URL

```
https://api.carditrack.com/api/v1
```

## Authentication

All endpoints require a JWT Bearer token issued by **Auth0 Universal Login** (Authorization Code + PKCE). The API validates tokens; it does not issue them.

```
Authorization: Bearer <access_token>
```

**Token policy** (see [auth.md](auth.md)): access tokens live 15–60 minutes; rotating refresh tokens have a 30-day absolute lifetime; web sessions idle out after ~15 minutes; on mobile the refresh token sits behind a biometric gate in secure storage.

## Versioning

All routes are prefixed with `/api/v1/`. Breaking changes will increment the version.

## Standard Error Format

```json
{
  "error": {
    "code": "RESOURCE_NOT_FOUND",
    "message": "CardiMember with id 'abc123' was not found.",
    "traceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
  }
}
```

| HTTP Status | When Used |
|-------------|-----------|
| 200 | Success |
| 201 | Resource created |
| 204 | Success, no content |
| 400 | Validation error / bad request |
| 401 | Missing or invalid token |
| 403 | Authenticated but not authorized |
| 404 | Resource not found |
| 409 | Conflict (e.g. duplicate invite) |
| 422 | Business rule violation |
| 500 | Internal server error |

## MVP Priority Legend

| Priority | Meaning |
|----------|---------|
| **P0** | Must Have — MVP launch blocker |
| **P1** | Should Have — MVP launch goal |
| **P2** | Nice to Have — post-launch sprint |
| **Future** | Post-MVP roadmap |

## API Domains

| File | Domain | Key User Stories |
|------|--------|-----------------|
| [auth.md](auth.md) | Authentication | 1.1, 10.2 |
| [cardimembers.md](cardimembers.md) | CardiMember Management | 1.2, 7.1, 7.2, 7.3 |
| [devices.md](devices.md) | Device Management | 1.3, 6.2 |
| [health-data.md](health-data.md) | Health Data & Dashboard | 2.1, 2.2, 2.3, 5.2, 10.1 |
| [alerts.md](alerts.md) | Alerts & Notification Preferences | 3.1, 3.2, 3.3, 11.1–11.3 |
| [family.md](family.md) | Family Collaboration | 4.1, 4.2, 8.3 |
| [notifications.md](notifications.md) | Push Notifications | 3.2, 5.1 |
| [subscriptions.md](subscriptions.md) | Subscription Management | 6.1 |
| [reports.md](reports.md) | Reports & Exports | 2.3, 9.2 |

## Related Documentation

- [Mobile User Stories](../../ui/mobile/user_stories.md)
- [Web User Stories](../../ui/web/user_stories.md)
- [Entity Summary](../../../technical/entity_summary.md)
- [Auth0 Integration](../../../technical/auth0_integration.md)
- [User Onboarding Process](../../../technical/user_onboarding_process.md)

---

**Document Version:** 1.0
**Last Updated:** March 2026
**Owner:** Backend Engineering Team
