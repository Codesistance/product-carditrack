# OAuth Clients — Inventory & Provisioning

CardiTrack runs **two separate OAuth systems** that must never be confused: one
authenticates *our users* (caregivers/family signing into the app), the other
authorizes *device data access* (the wearer consenting to share wearable data).
They authenticate different people, in different consoles, with different token
lifecycles.

| | Identity (who signs in) | Device data (whose data flows) |
|---|---|---|
| Person | Caregiver / family member | Wearer (often not an app user — authorizes via invitation link) |
| Authorization server | Auth0 tenant | Provider's own (Google for Fitbit/Pixel, Garmin, …) |
| Tokens | Short-lived JWTs for our API; session-scoped | Long-lived refresh tokens, AES-256-GCM-encrypted in `DeviceConnections`; Worker refreshes indefinitely |
| Configured in | Auth0 dashboard ([runbook](./auth0_setup_runbook.md)) | Each provider's developer console |

## Client inventory

| # | Client | System | Type | Console | Secrets | Status |
|---|--------|--------|------|---------|---------|--------|
| 1 | CardiTrack Web (`Auth0__ClientId/Secret`) | Identity | Confidential (Regular Web App) | Auth0 | `auth0-client-id` / `auth0-client-secret` | Created per [runbook §4](./auth0_setup_runbook.md) |
| 2 | CardiTrack Mobile | Identity | Public (Native, PKCE, no secret) | Auth0 | `auth0-mobile-client-id` | Created per [runbook §3](./auth0_setup_runbook.md) |
| 3 | Google sign-in (social) | Identity | Web app client **used by Auth0**, not by our code | Google Cloud | Stored inside the Auth0 connection | Pending (Phase 9, below) |
| 4 | Apple Sign In (social) | Identity | Services ID + .p8 key **used by Auth0** | Apple Developer | Stored inside the Auth0 connection | Pending (Phase 9, below) |
| 5 | Fitbit provider (Google Health API) | Device data | Confidential Web application | Google Cloud | `fitbit-client-id` / `fitbit-client-secret` | Code merged (PR #10); console registration pending |
| 6+ | Garmin / Withings / Oura / Whoop | Device data | Per-vendor | Each vendor's portal | Not yet provisioned | Future — config stubs only; **only Fitbit is registered in DI** |

Related shared secret: `carditrack-{env}-encryption-key` (`Encryption__Key`) — the
AES-256-GCM key protecting the device-data tokens stored in `DeviceConnections`.
It belongs to no single OAuth client but every device-data flow depends on it.

> **The #3 vs #5 foot-gun:** both are "Google OAuth clients" in the same Google
> Cloud organisation, but they are different registrations with different
> purposes. #3 asks for `openid profile email` so a caregiver can *sign in*;
> #5 asks for restricted `googlehealth.*` scopes so a wearer can *share heart
> data*. Never reuse one for the other — mixing them would drag the sign-in
> client into Google's restricted-scope verification, and put health scopes on
> a login button.

All non-Apple registrations live under the cloud-ops Google account
(cloudoperations@codesistance.com); Apple uses the same account's Apple
Developer membership.

---

## Social log-on (Phase 9) — scope

The mobile app already renders **Google** and **Apple** buttons on **both**
`CreateAccountPage` and `SignInPage`, and the Auth0 Native app already allows
the `carditrack://oauth/callback` callback and Authorization Code + PKCE grant
([runbook §3](./auth0_setup_runbook.md)). The buttons are **unwired** — they
have no tap handlers, and the app-side PKCE invocation is still to build — so
the remaining work is **app code + credentials + Auth0 connection config**.
Microsoft (`windowslive`) is not planned for MVP and has no button in the
mobile UI — treat it as deferred until product asks.

**What's needed per provider:**

### Google (`google-oauth2` connection)

1. Google Cloud console (cloud-ops account) → **APIs & Services → Credentials →
   Create OAuth client**, type **Web application**, name `CardiTrack Sign-In
   ({env})` — a separate client from the Health API one (#5).
2. Authorized redirect URI: `https://{auth0-tenant-domain}/login/callback`
   (custom-domain tenants use that domain instead).
3. Consent screen: only non-sensitive scopes (`openid`, `profile`, `email`) —
   **no restricted-scope review applies to this client**; basic branding
   verification only.
4. Auth0 dashboard → **Authentication → Social → Google** → paste client ID +
   secret → enable for the **CardiTrack Mobile** app (and Web when its login
   ships).

> Auth0's built-in **dev keys** make the Google connection work with zero
> setup, but they show Auth0 branding on the consent screen, break SSO/silent
> auth, and are unsuitable beyond a smoke test. Use our own keys from step 1
> in every environment.

### Apple (`apple` connection)

Required by App Store review: an iOS app offering any third-party social login
(our Google button) **must** also offer Sign in with Apple.

1. Apple Developer portal (cloud-ops membership): App ID with the **Sign In
   with Apple** capability (the existing app ID), plus a **Services ID** — this
   acts as the client ID for web-based flows.
2. Register the return URL on the Services ID:
   `https://{auth0-tenant-domain}/login/callback`.
3. Create a **Sign in with Apple private key** (.p8) and record the **Key ID**
   and **Team ID**.
4. Auth0 → **Authentication → Social → Apple** → enter Services ID, Team ID,
   Key ID, and the .p8 contents → enable for the Mobile app.

### Cross-cutting items (both providers)

- **Email verification**: social identities arrive pre-verified, but the Action
  deployed per [runbook §8](./auth0_setup_runbook.md) denies **all** unverified
  logins with no social exception — Phase 9 must **add a short-circuit** for
  `google-oauth2`/`apple` so social users aren't blocked by the gate.
- **Account linking**: the same person arriving via password and via Google
  creates two Auth0 identities with the same email. The API keys users on the
  `sub` claim, so unlinked duplicates become two CardiTrack users. Decide
  before launch: enable Auth0 account-linking or accept distinct accounts.
- **Per-environment**: everything above is per Auth0 tenant — repeat for dev
  and prod, and keep prod's Google/Apple credentials out of the repo (they live
  only in the Auth0 dashboard).

---

## Provisioning steps — identity clients (Auth0)

Fully scripted in the [Auth0 setup runbook](./auth0_setup_runbook.md); summary:

1. Tenant + API (resource server) — runbook §§1–2.
2. **CardiTrack Mobile** (Native, public, PKCE; Password grant for the embedded
   login; refresh-token rotation) — §3.
3. **CardiTrack Web** (Regular Web App, confidential; Client Credentials only
   for the narrow Management API grant) — §4.
4. Tenant settings, attack protection, email, post-login Action — §§5–8.
5. Populate Secret Manager and roll out: `scripts/set-auth0-secrets.sh <env>` — §11.
6. Social connections (Phase 9) — section above.

## Provisioning steps — device-data client (Google Health API)

> **[PR #10](https://github.com/Codesistance/product-carditrack/pull/10) is merged** — the
> `GET /api/v1/oauth/redirect/{provider}` bounce endpoint and the
> `DeviceProviders` Google configuration exist in the codebase; only the
> **deploy** is pending. Until a deployed revision includes it, the redirect
> URIs below point at an endpoint that isn't live yet. Steps 1–2 (API
> enablement, consent screen, test users) can be done any time.

Flow security notes: the `state` values in this flow are **opaque,
server-cached, single-use tokens with a 15-minute TTL** (never encode member
ids in them), and the bounce endpoint only ever redirects into the
`carditrack://` scheme — any other target would be an open redirect.

1. Google Cloud console (cloud-ops account), project per environment (or the
   existing `carditrack-{env}` project): **enable the Google Health API**.
2. **OAuth consent screen**: External; app name `CardiTrack`; support email +
   branding matching the public homepage; add the restricted scopes
   `googlehealth.activity_and_fitness.readonly`,
   `googlehealth.health_metrics_and_measurements.readonly`,
   `googlehealth.sleep.readonly`; add **test users** (dev/beta wearers' Google
   accounts) — until verification passes, only they can connect, max 100.
3. **Create OAuth client**, type **Web application**, name
   `CardiTrack Devices ({env})`. Authorized redirect URIs are the API's
   **bounce endpoint** — never the `carditrack://` deep link (Google rejects
   custom schemes on web clients):
   - dev: `https://localhost:7001/api/v1/oauth/redirect/fitbit` and the
     deployed dev API URL + `/api/v1/oauth/redirect/fitbit`
   - prod: `https://api.carditrack.com/api/v1/oauth/redirect/fitbit`
4. Populate Secret Manager (after `terraform apply` creates the secrets):
   ```bash
   printf '%s' "$CLIENT_ID"     | gcloud secrets versions add carditrack-{env}-fitbit-client-id     --data-file=-
   printf '%s' "$CLIENT_SECRET" | gcloud secrets versions add carditrack-{env}-fitbit-client-secret --data-file=-
   ```
   then roll new API + Worker revisions so the env bindings pick up the values.
5. **Sandbox verification**: with a test user connected, exercise a sync and
   compare `FitbitApiClient`'s parsing against real payloads. The v4 reference
   only documents some rollup value schemas, so several field names were
   inferred from the documented naming convention (PR #10): the
   distance/active-minutes/total-calories/floors rollup values, the
   resting-heart-rate union member, and the sleep session shape. Confirm each
   and fix any mismatches.
6. **Before public launch**: restricted-scope verification + CASA assessment —
   prerequisites checklist in
   [user_onboarding_process.md](./user_onboarding_process.md) (Step 6). Status:
   the Google-format in-app disclosure banner shipped on Web (PR #9); the
   mobile equivalent is still pending.

Future device providers (Garmin, Withings, …) repeat steps 3–4 in their own
portals with their own `DeviceProviders` entry and secrets; the multi-provider
config, keyed DI, and (if the vendor requires https redirects) the bounce
endpoint generalize as-is.
