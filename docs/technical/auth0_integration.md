# AUTH0 INTEGRATION - CARDITRACK

## OVERVIEW

CardiTrack uses **Auth0** as its authentication backend: Auth0 owns credentials and issues the RS256 JWTs the API validates. This document covers **why Auth0, the design decisions, and tenant policy**. All operational detail (tenant setup, application configuration, secrets, verification curls) lives in the [Auth0 setup runbook](./auth0_setup_runbook.md); the client inventory and social-connection provisioning live in [oauth_clients.md](./oauth_clients.md). Where this document and the runbook disagree, the runbook wins.

---

## WHAT IS IMPLEMENTED TODAY

The shipped authentication flow is **not** the Universal Login redirect — mobile uses the **embedded password-realm grant** with native screens:

- **Login**: the mobile app (`Auth0AuthClient`) posts credentials directly to `/oauth/token` with `grant_type=http://auth0.com/oauth/grant-type/password-realm` (realm `Username-Password-Authentication`), receiving access + refresh tokens without leaving the app.
- **Signup**: `POST /dbconnections/signup` from the app's Create Account screen.
- **Password reset**: `POST /dbconnections/change_password` from the Forgot Password flow.
- **Email-verification gate**: a single tenant **post-login Action** denies every unverified login with the exact deny reason `email_not_verified` — this string is an exact-match contract with the app, which routes the user to its Verify Email screen. The same Action stamps the namespaced claim `https://carditrack.com/email_verified` into the access token.
- **Claim consumption**: the API's `UserContextMiddleware` reads `https://carditrack.com/email_verified`; the stored `User.EmailVerified` flag is refreshed on every `GET /api/Onboarding/status`.
- **Resend verification**: `POST /api/v1/auth/resend-verification` (`AllowAnonymous`, rate-limited 5/hour/IP, always answers success to prevent user enumeration). It is backed by a hand-rolled `HttpClient`-based `Auth0ManagementClient` whose only operation is `TrySendVerificationEmailAsync` — client-credentials token (cached until near expiry) + `api/v2/jobs/verification-email`. There is no Auth0.ManagementApi SDK dependency.
- **Authorization policies**: `RequireAdmin`, `RequireBusinessAccount`, `RequireFamilyAccount` plus a global `FallbackPolicy` (require authenticated user) are registered in `Auth0Extensions`. The three claim-based policies are **inert today** — the tenant does not yet issue `role`/`organization_type` claims (see runbook §13); the API derives identity from the `sub` claim + database lookup.
- **Social login (wired 2026-08-10)**: the Google and Apple buttons on `CreateAccountPage` and `SignInPage` launch Auth0 **Universal Login** in the system browser (Authorization Code + PKCE + state, `connection=google-oauth2|apple`), exchange the code at `/oauth/token`, and join the normal post-login routing. Same-email unification is tenant-side: the post-login Action links a first social login into an existing verified database account and re-keys the session so the `sub` stays `auth0|…` (runbook §8); the API backstops unlinked duplicates with a 409 from onboarding (`DuplicateEmailException`). Working end-to-end requires the per-tenant Action + connection enablement; Apple additionally awaits its credentials ([oauth_clients.md](./oauth_clients.md)).
- **CardiTrack.Web**: has **no auth wiring at all**. The Web/API Regular Web Application client exists so the API's Management API grant and future Blazor login have real credentials.

> **Status: Planned — not yet implemented.** Universal Login for web, MFA, and enterprise SSO (SAML / Azure AD / Okta) are future work (social handlers + account linking shipped 2026-08-10, pending per-tenant Action deploy). Microsoft (`windowslive`) and Facebook connections are **not planned for MVP** and have no UI. There are no `/api/auth/check-status` or `/api/auth/sync-user` endpoints, and no legacy Auth0 Rules — the single post-login Action in [runbook §8](./auth0_setup_runbook.md) supersedes the four-rule design that used to live in this document.

---

## WHY AUTH0

- **Credential outsourcing**: no password verification logic in CardiTrack. (Note: a legacy `PasswordHash` column remains on the `Users` table pending removal — it is never written with a real hash; credentials are Auth0-hosted.)
- **HIPAA BAA available**: Auth0 offers a BAA and compliance mode — required before prod go-live (runbook §1).
- **Attack protection built in**: brute-force protection, breached-password detection. Note the embedded password-grant flow **cannot render a captcha**, so bot detection stays off (runbook §6); the escape hatch is switching to Universal Login later.
- **Cheaper than building it**: HIPAA-compliant auth plus verification email delivery, token rotation, and anomaly detection for a per-MAU fee.

---

## TENANT POLICY

| | dev | prod |
|---|---|---|
| Tenant | `carditrack-dev` | `carditrack-prod` |
| Region | **UK** (aligns with GCP europe-west2/London) | **UK or EU** (GCP europe-west2 data residency) |
| HIPAA BAA | not required | sign BAA + enable compliance mode **before go-live** |

- **One API identifier across tenants**: `https://api.carditrack.com` on both dev and prod. Cross-tenant isolation is enforced by issuer + signature validation (decision 2026-08-04, runbook §2).
- **Token policy**: access tokens 3600s; refresh tokens rotating, 30-day absolute lifetime, 15-day inactivity lifetime (runbook §§2–3).
- **Applications**: `CardiTrack Mobile` (Native, public, no secret; Password + Authorization Code + Refresh Token grants) and `CardiTrack Web` (Regular Web App, confidential; Client Credentials only for the narrow Management API grant). Full setup: runbook §§3–4.
- **Management API scopes**: exactly `read:users` + `update:users`, granted to the Web/API application (runbook §9). No broader scopes.

---

## CONNECTIONS

### Database (email/password) — implemented

`Username-Password-Authentication`, the tenant Default Directory. Sign-ups enabled (the app registers via `/dbconnections/signup`); password policy `Good` or stronger — the policy text surfaces verbatim in the app's weak-password error banner (runbook §5).

### Social — app-side shipped 2026-08-10; tenant work per environment

Google (`google-oauth2`) and Apple (`apple`) only. The app-side flow (Universal Login, code + PKCE) is wired on both Create Account and Sign In pages; the remaining work is per-tenant — enable the connections for CardiTrack Mobile, deploy the runbook §8 Action (verification short-circuit + account linking), and provision Apple's credentials — provisioning steps in [oauth_clients.md](./oauth_clients.md). Note the sign-in Google client is a **different registration** from the Google Health API device-data client.

### Enterprise SSO — planned, post-MVP

> **Status: Planned — not yet implemented.** SAML 2.0 / Azure AD / Okta connections for business accounts are a future capability with no code, no tenant configuration, and no committed release.

---

## CONFIGURATION

Canonical configuration keys (from `ConfigurationKeys.cs` — these are the **only** Auth0 keys the code reads):

```json
{
  "Auth0": {
    "Domain": "{tenant-domain, bare host}",
    "Audience": "https://api.carditrack.com",
    "ClientId": "{CardiTrack Web client id}",
    "ClientSecret": "{CardiTrack Web client secret}",
    "CallbackUrl": "https://localhost:7001/api/auth/callback",
    "LogoutUrl": "https://localhost:7001/"
  }
}
```

There is no `JwtBearer` section (JWT validation is wired in code from `Auth0:Domain` + `Auth0:Audience`), no `Auth0:ManagementApi:*` (the management client reuses `Auth0:ClientId`/`ClientSecret`), and no `Auth0:Connections:*` section.

**Deployment**: values arrive as env vars (`Auth0__Domain`, `Auth0__Audience`, `Auth0__ClientId`, `Auth0__ClientSecret`) bound to Secret Manager secrets `carditrack-{env}-auth0-domain`, `-audience`, `-client-id`, `-client-secret`, plus `carditrack-{env}-auth0-mobile-client-id` stamped into mobile builds at build time. Populate with `scripts/set-auth0-secrets.sh <env>` (runbook §11).

**Callback URLs (reality)**:
- Blazor dev runs at `https://localhost:7177` (not 7001/7002 — those appear only in stale config).
- Mobile deep link: `carditrack://oauth/callback` (allowed on the Native app for Phase 9 social login).
- Web prod: `https://app.carditrack.com/callback`.

---

## COST ESTIMATE

**Auth0 Pricing (HIPAA-Compliant Plan):**
- **Professional Plan**: ~$240/month (up to 1,000 active users), BAA included
- **Additional Users**: ~$0.40/month per additional MAU
- 10,000+ MAU: Enterprise pricing (contact sales)

Still cheaper than building custom auth + HIPAA compliance in-house.

---

## SUPPORT & RESOURCES

- [Auth0 setup runbook](./auth0_setup_runbook.md) — operator steps, per environment
- [oauth_clients.md](./oauth_clients.md) — client inventory, social provisioning (Phase 9)
- [Auth0 HIPAA Compliance Guide](https://auth0.com/docs/compliance/hipaa)
- [Management API Reference](https://auth0.com/docs/api/management/v2)

---

**Last Updated:** August 7, 2026

**END OF AUTH0 INTEGRATION DOCUMENTATION**
