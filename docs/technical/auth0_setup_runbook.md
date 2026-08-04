# Auth0 Setup Runbook (Operator)

Step-by-step tenant configuration matching the **implemented** mobile auth (embedded
password-realm grant + native screens) and the API's JWT validation. Run once per
environment (dev, prod). Supersedes the application-setup section of
[auth0_integration.md](./auth0_integration.md) where they disagree.

Secrets are populated at the end with `scripts/set-auth0-secrets.sh <env>`.

---

## 1. Tenant

| | dev | prod |
|---|---|---|
| Tenant name | `carditrack-dev` | `carditrack-prod` |
| Region | EU | EU (align with GCP europe-west2 data residency) |
| Environment tag | Development | Production |
| HIPAA BAA | not required | contact Auth0 sales, sign BAA, enable compliance mode **before go-live** |

## 2. Register the API (resource server)

Auth0 Dashboard → **Applications → APIs → Create API**:

- **Name**: `CardiTrack API`
- **Identifier** (= the `audience` value; a logical URI, never called — set once, it
  cannot be renamed later):
  - dev tenant: `https://api.dev.carditrack.com`
  - prod tenant: `https://api.carditrack.com`

  Cross-tenant isolation is enforced by issuer + signature validation regardless of
  this string, but per-env identifiers add a second guard (a mispointed
  `Auth0__Domain` alone can't make one env accept the other's tokens) and make a
  decoded token's `aud` self-identify its environment. The value is never hardcoded:
  it flows from the `carditrack-{env}-auth0-audience` secret into both the API and
  the mobile build stamp.
- **Signing Algorithm**: RS256
- Settings after creation:
  - **Allow Offline Access**: ON — without this no refresh token is ever issued and mobile sessions die when the access token expires.
  - **Token Expiration**: 3600s (access token; 900–3600 acceptable per auth.md).
  - Token Expiration (browser flows): 3600s.

## 3. Mobile application (Native)

**Applications → Create Application → Native**, name `CardiTrack Mobile`.

- **Advanced Settings → Grant Types** — check exactly:
  - `Authorization Code` (social login via PKCE, Phase 9)
  - `Refresh Token`
  - `Password` (the embedded email/password login — this is the one that's off by default)
  - **Uncheck `Implicit`** — it's pre-checked on new apps but deprecated (tokens in
    the URL fragment) and unused by any CardiTrack flow; on a public client every
    enabled grant is an open door.
- Native apps are public clients — there is no client secret; the **Client ID** is the value for the `carditrack-{env}-auth0-mobile-client-id` secret.
- **Refresh Token Rotation** (Settings → Refresh Token Rotation):
  - Rotation: ON, Reuse Interval: 0
  - Absolute Lifetime: 2592000 (30 days, per auth.md)
  - Inactivity Lifetime: 1296000 (15 days)
- **Allowed Callback URLs** (needed for Phase 9 social login; harmless to set now):
  - `carditrack://oauth/callback`
- **Allowed Logout URLs**: `carditrack://oauth/callback`
- **Connections tab**: enable `Username-Password-Authentication` (and later `google-oauth2` / `apple`).

## 4. Web/API application (Regular Web Application)

A confidential client whose credentials fill the API's `Auth0__ClientId` /
`Auth0__ClientSecret` env bindings today, and will serve the Blazor web login when
that's built (CardiTrack.Web has no auth wiring yet — this is created ahead of need
so the secrets aren't placeholders).

**Applications → Create Application → Regular Web Applications**, name
`CardiTrack Web`. In the wizard, technology choice is cosmetic — skip or pick
ASP.NET Core. Then in **Settings**:

- **Domain / Client ID / Client Secret** (top of page): the id and secret are the
  values for `carditrack-{env}-auth0-client-id` / `carditrack-{env}-auth0-client-secret`
  (reveal the secret with the eye icon; treat it as a credential — Secret Manager
  only, never in the repo).
- **Allowed Callback URLs** (ASP.NET Core Auth0 SDK's default callback path is
  `/callback`):
  - dev tenant: `https://app.dev.carditrack.com/callback, https://localhost:7177/callback`
  - prod tenant: `https://app.carditrack.com/callback`
  (Local Blazor runs at `https://localhost:7177` per its launchSettings — the
  `localhost:7002` seen in older config is stale.)
- **Allowed Logout URLs**:
  - dev tenant: `https://app.dev.carditrack.com, https://localhost:7177`
  - prod tenant: `https://app.carditrack.com`
- **Advanced Settings → Grant Types**: `Authorization Code` + `Refresh Token` +
  `Client Credentials` — **uncheck `Implicit`** (pre-checked, deprecated) and leave
  `Password` off. Client Credentials exists solely for the narrow Management API
  grant in section 9 (read:users + update:users, powering resend-verification);
  grant no broader scopes to this application.
- **Credentials tab** (newer dashboards; older tenants show this as a "Token
  Endpoint Authentication Method" dropdown at the bottom of Settings): under
  Application Authentication, verify **Client Secret (Post)** — the default is
  correct. This is how the confidential client proves itself at `/oauth/token`;
  the Native mobile app's Credentials tab shows **None** by design.
- **Connections tab** (rightmost tab on the application page): under *Database*,
  ensure `Username-Password-Authentication` is toggled ON — it usually already is
  (connections default to enabled-for-all-applications). Same check applies to the
  mobile app; the connection's own **Applications** tab shows the same links from
  the other side.

## 5. Tenant-level settings

- **Settings → General → API Authorization Settings → Default Directory** =
  `Username-Password-Authentication`. Required for the password grant (the app also
  sends `realm=`, but set it anyway).
- **Authentication → Database → Username-Password-Authentication**:
  - Ensure the connection exists and **Disable Sign Ups is OFF** (the app registers
    via `/dbconnections/signup`).
  - **Password Policy**: `Good` or stronger. The policy text is surfaced verbatim in
    the app's weak-password error banner.

## 6. Attack protection (Security → Attack Protection)

| Feature | dev | prod | Why |
|---|---|---|---|
| Brute-force protection | ON | ON | returns `too_many_attempts`, which the app maps to a friendly message |
| Breached password detection | OFF | ON (block) | works with the password grant |
| **Bot detection** | **OFF** | OFF (see note) | a captcha challenge cannot be rendered in the embedded flow — it hard-breaks password-grant logins |

Note: if prod bot-protection is required later, the escape hatch is switching
email/password to Universal Login (browser-based), which supports captcha.

## 7. Email (prod-critical)

Auth0's built-in email sender is for development only (heavily rate-limited, may be
junk-filtered). Before prod:

- **Branding → Email Provider**: configure a real provider (SendGrid/Mailgun/SMTP)
  using the cloudoperations@codesistance.com account.
- **Branding → Email Templates**: customize *Change Password* (this is the email the
  app's Forgot Password flow triggers) and *Verification Email*.

## 8. Post-login Action (verification gate + claims)

The tenant enforces a **hard email-verification gate**: unverified logins are denied, and
the app routes users to its Verify Email screen. The deny reason MUST be the exact string
`email_not_verified` — the app maps it to that screen; prose reasons degrade to a generic
error. The same Action copies `email_verified` into the access token so the API records
real verification state.

**Actions → Library → Create Action** ("CardiTrack post-login", Login / Post Login), then
drag it into **Actions → Triggers → post-login** (it must be the ONLY Action denying
logins — remove any earlier verification Action):

```js
exports.onExecutePostLogin = async (event, api) => {
  const ns = 'https://carditrack.com';
  api.accessToken.setCustomClaim(`${ns}/email_verified`, event.user.email_verified);

  if (!event.user.email_verified) {
    api.access.deny('email_not_verified'); // exact string — the app matches it
  }
};
```

The API reads `https://carditrack.com/email_verified` (see `UserContextMiddleware`) at
user creation and refreshes it on every `GET /api/Onboarding/status`. Role/organization
claims can be added to this same Action later (section 13).

## 9. Authorize the Management API (resend verification email)

The API's `POST /api/v1/auth/resend-verification` endpoint (used by the app's Verify
Email screen) calls the Auth0 Management API with the Web/API application's client
credentials:

1. **Applications → APIs → Auth0 Management API → Machine to Machine Applications**.
2. Toggle the **Web/API application** (section 4) to *Authorized*.
3. Expand it and grant exactly two scopes: `read:users`, `update:users`. Update.

Without this, resends fail server-side (logged as "management token request failed") but
the endpoint still answers 200 — users just don't get a second email.

Because logins are gated on the email arriving, **section 7 (real email provider) is
blocking for prod** — with the dev sender, verification mails may be junk-filtered and
users locked out.

## 10. Test user

**User Management → Users → Create User** (connection `Username-Password-Authentication`),
e.g. `carditrack-test@codesistance.com`, for the verification curls and app QA.

## 11. Populate Secret Manager and roll out

```bash
bash scripts/set-auth0-secrets.sh dev     # prompts for domain/audience/client ids/secret
```

Then force a Cloud Run rollout (or let the next deploy do it) — secret-backed env
vars are resolved at instance start:

```bash
gcloud run services update carditrack-dev-api --region=europe-west2 \
  --project=carditrack-490120 --update-labels=auth0-config-rollout=$(date +%s)
```

## 12. Verify (before blaming app code)

Password grant issues both tokens:

```bash
curl -s -X POST https://<tenant-domain>/oauth/token \
  -d 'grant_type=http://auth0.com/oauth/grant-type/password-realm' \
  -d 'realm=Username-Password-Authentication' \
  -d 'client_id=<mobile-client-id>' \
  -d 'audience=<api-identifier-for-this-tenant>' \
  -d 'scope=openid profile email offline_access' \
  -d 'username=<test-user>' -d 'password=<password>'
# MUST contain access_token AND refresh_token.
# Decode the access token (jwt.io or `jq -R 'split(".")[1] | @base64d | fromjson'`) and
#   check the https://carditrack.com/email_verified claim is present → step 8 if missing.
# No refresh_token → API "Allow Offline Access" is off or the Refresh Token grant
#   is unchecked on the Native app.
# "authorization_server ... not configured with default directory" → step 5.
# "Grant type ... not allowed" → Password grant unchecked (step 3).
```

API accepts the token:

```bash
AT=<access_token from above>
curl -s -H "Authorization: Bearer $AT" https://api.dev.carditrack.com/api/Onboarding/status
# Expect a 200 ApiResponse envelope with hasUserAccount:false for a fresh user.
# 401 → API's Auth0__Domain/Audience secrets don't match the tenant/identifier,
#   or the service hasn't rolled a new revision since secrets were set.
```

Signup + password reset endpoints:

```bash
curl -s -X POST https://<tenant-domain>/dbconnections/signup \
  -H 'Content-Type: application/json' \
  -d '{"client_id":"<mobile-client-id>","email":"new@example.com","password":"S0me-Strong-Pass!","connection":"Username-Password-Authentication","name":"Test User"}'

curl -s -X POST https://<tenant-domain>/dbconnections/change_password \
  -H 'Content-Type: application/json' \
  -d '{"client_id":"<mobile-client-id>","email":"new@example.com","connection":"Username-Password-Authentication"}'
# Always 200 with a plain-text body; the reset email should arrive.
```

## 13. Later (not blocking first sign-in)

- **Social login (Phase 9)**: enable `google-oauth2` + `apple` connections (Google
  Cloud OAuth credentials / Apple Services ID per auth0_integration.md), attach them
  to the Native app; the app already renders the buttons.
- **More claims in the section 8 Action** (`https://carditrack.com/role`,
  `.../organization_id`, `email`) — the API currently derives the user from the
  `sub` claim + database lookup, so these are optional until the role policies
  (`RequireAdmin` etc.) are exercised.
