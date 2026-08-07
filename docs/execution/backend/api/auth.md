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

The API reads only **three** claims from the access token (via `UserContextMiddleware`):

| Claim | Used for |
|-------|----------|
| `sub` | Auth0 user ID — the key linking the token to the local `Users` row |
| `email` | Identity email (onboarding overwrites any client-supplied email with this) |
| `https://carditrack.com/email_verified` | Verification state, set by the tenant's post-login Action (a bare `email_verified` claim is accepted as fallback for tests/other issuers; absent → `null`) |

```json
{
  "sub": "auth0|65f1c2...",
  "email": "jane@example.com",
  "https://carditrack.com/email_verified": "true",
  "exp": 1704844800
}
```

**Role and organization do not come from claims.** After token validation, the middleware looks the user up in the **database** by `Auth0UserId` and enriches the request context with the local `UserId`, `OrganizationId`, and `Role`. Roles are `Member`, `Admin`, and `Staff` (integer enum `UserRole` — there is no `Viewer` role).

> Three authorization policies (`RequireAdmin`, `RequireBusinessAccount`, `RequireFamilyAccount`) are registered but **used by no endpoint**. They require bare `role` / `organization_type` claims that no Auth0 Action currently issues — if wired up as-is they would deny everyone. Treat them as scaffolding.

### Token-derived identity (onboarding)

Onboarding endpoints deliberately **overwrite identity fields in the request body from the request context** (introduced in PR #5):

- `email` — taken from the token's email claim (body value is only a fallback when the claim is absent)
- `auth0UserId` and `emailVerified` — token-only; never read from the body
- `locale` — parsed from the `Accept-Language` header (first language tag; default `en-US`)

The `Users.Auth0UserId` column has a **unique filtered index** (excluding empty strings), making onboarding retries idempotent per Auth0 identity.

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

- **Logout**: client discards tokens and calls Auth0 `/oidc/logout`. (Push-token unregistration will be added when the notifications domain ships — see [notifications.md](notifications.md), currently planned.)
- **Admin-initiated removal** (family member removed from account): the API rejects further requests at the authorization layer immediately, regardless of remaining token validity, because org membership is checked against the database.
- **Suspicious activity**: sessions can be revoked tenant-wide via the Auth0 Management API.

---

## API Endpoint

### POST `/api/v1/auth/resend-verification`

The only implemented endpoint in this domain. Resends Auth0's verification email via the Management API. **Anonymous by design** — the caller cannot log in until verified — and rate-limited to **5 requests/hour/IP**.

**Priority:** P0 | **Auth Required:** No

#### Request Body

```json
{
  "email": "jane@example.com"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `email` | string | Yes | Address to resend verification to (validated for email format) |

#### Response `200 OK` — always, wrapped `ApiResponse<bool>`

```json
{
  "success": true,
  "message": "If that email is registered with us, a verification link is on its way!",
  "data": true,
  "timestamp": "2026-08-07T10:00:00Z"
}
```

> **Non-enumerating:** the endpoint answers 200 whether or not the email exists — there is no signal distinguishing registered from unregistered addresses. A malformed email returns 400 (validation).

---

## Errors

Authentication errors surface as standard API errors (see [readme.md](readme.md)). **There are no machine-readable error codes on the wire** — the `ErrorResponse` body carries only a human-readable `message`; clients branch on HTTP status:

| Status | Description |
|--------|-------------|
| 401 | Missing, expired, or invalid access token (validated with zero clock skew) |
| 403 | Token valid but no local user row yet (onboarding incomplete), or member-link authorization failed |
| 429 | Rate limit exceeded (see [readme.md](readme.md)) |

> **Email verification is not enforced by the API.** The verification state from the token is persisted on the user record and echoed in onboarding status, but no API endpoint rejects unverified users — the gate is the **Auth0 post-login Action**, which blocks login until the email is verified. There is no `EMAIL_NOT_VERIFIED` API error.

---

**Related:** [readme.md](readme.md) | [Auth0 Integration](../../../technical/auth0_integration.md) | [User Onboarding](../../../technical/user_onboarding_process.md) | [User Stories 1.1, 10.2](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026
