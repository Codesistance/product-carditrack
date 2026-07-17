# Authentication API

CardiTrack authentication is **Auth0-hosted**. All credential handling (email/password, Google, Apple) happens on Auth0 Universal Login — the CardiTrack API never sees or stores passwords and issues no tokens of its own. The API's role is limited to validating Auth0-issued JWTs and provisioning the local user record on first login.

**User Stories:** 1.1 (First-Time User Registration), 10.2 (Biometric Login)

---

## Authentication Flow (Universal Login + PKCE)

Both web and mobile use the OAuth 2.0 Authorization Code flow with PKCE against Auth0:

1. Client redirects to the Auth0 Universal Login page (`https://{tenant}.auth0.com/authorize`) with a PKCE challenge.
2. User authenticates (email/password, Google, or Apple — all configured in Auth0).
3. Auth0 redirects back to the client (`carditrack://oauth/callback` on mobile) with an authorization code.
4. Client exchanges the code (plus PKCE verifier) with Auth0 for an **access token**, **ID token**, and **refresh token**.
5. Client calls the CardiTrack API with `Authorization: Bearer <access_token>`.
6. On first authenticated call, the API provisions the local `Users` row (keyed by `Auth0UserId`) — see [Onboarding](../../../technical/user_onboarding_process.md).

New-user requirements (terms acceptance, organization creation) are enforced during onboarding, not at the Auth0 layer — see the [onboarding process](../../../technical/user_onboarding_process.md).

> There are **no** `POST /auth/register`, `POST /auth/login`, or `POST /auth/social` endpoints. Any doc referencing them is outdated.

---

## Token Policy

| Token | Lifetime | Notes |
|-------|----------|-------|
| Access token | **15–60 minutes** | Validated by the API on every request (issuer, audience, expiry, signature) |
| Refresh token | **Rotating; 30-day absolute lifetime** | Auth0 refresh token rotation enabled; reuse detection revokes the family |
| Web session | **~15-minute idle timeout** | Cookie session; silent token renewal while active |
| Mobile session | Refresh-token backed | Refresh token stored in platform secure storage (Keychain / Keystore), gated by biometrics (below) |

Token refresh is performed **directly against Auth0** (`POST https://{tenant}.auth0.com/oauth/token` with `grant_type=refresh_token`) — the CardiTrack API does not proxy token refresh.

### JWT claims consumed by the API

```json
{
  "sub": "auth0|65f1c2...",
  "email": "jane@example.com",
  "https://carditrack.com/role": "Admin",
  "https://carditrack.com/organization_id": "450e8400-...",
  "exp": 1704844800
}
```

Roles (`Admin`, `Staff`, `Viewer`) and `organization_id` are added to the access token by an Auth0 post-login Action — see [Auth0 Integration](../../../technical/auth0_integration.md).

---

## Biometric Login (Face ID / Touch ID)

Biometrics are a **local device gate, not a server-side credential**. No biometric key material is registered with or verified by the CardiTrack API.

- On opt-in, the mobile app moves the Auth0 refresh token into biometric-protected secure storage (iOS Keychain with `biometryCurrentSet` access control; Android Keystore with `setUserAuthenticationRequired`).
- On app open, the OS biometric prompt unlocks the refresh token; the app silently obtains a fresh access token from Auth0.
- If biometric unlock fails, is unavailable, or the refresh token has passed its 30-day absolute lifetime, the app falls back to Universal Login.
- Logout, biometric enrollment change (OS-enforced), or remote session revocation in Auth0 invalidates the stored refresh token.

**Priority:** P1 (MVP 3 on mobile — see [release matrix](../../../release_matrix.md))

---

## Session Revocation

- **Logout**: client discards tokens and calls Auth0 `/oidc/logout`; the mobile app also unregisters its push token (`DELETE /api/v1/notifications/devices/{tokenId}` — see [notifications.md](notifications.md)).
- **Admin-initiated removal** (family member removed from account): the API rejects further requests at the authorization layer immediately, regardless of remaining token validity, because org membership is checked against the database.
- **Suspicious activity**: sessions can be revoked tenant-wide via the Auth0 Management API.

---

## Errors

Authentication errors surface as standard API errors (see [readme.md](readme.md)):

| Code | Status | Description |
|------|--------|-------------|
| `UNAUTHORIZED` | 401 | Missing, expired, or invalid access token |
| `EMAIL_NOT_VERIFIED` | 403 | Auth0 email verification pending (database connections) |
| `ACCOUNT_SUSPENDED` | 403 | User suspended in CardiTrack; enforced by Auth0 Action + API |
| `INSUFFICIENT_PERMISSIONS` | 403 | Authenticated but role does not permit the operation |

---

**Related:** [readme.md](readme.md) | [Auth0 Integration](../../../technical/auth0_integration.md) | [User Onboarding](../../../technical/user_onboarding_process.md) | [User Stories 1.1, 10.2](../../ui/mobile/user_stories.md)
