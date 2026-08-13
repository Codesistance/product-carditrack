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
| 3 | Google sign-in (social) | Identity | Web app client **used by Auth0**, not by our code | Google Cloud (`carditrack-signin`) | Stored inside the Auth0 connection | **Provisioned 2026-08-07** — clients created, both tenants' Auth0 connections wired; **app buttons wired 2026-08-10** (Universal Login + PKCE); **Applications → CardiTrack Mobile toggle enabled in dev 2026-08-10** — prod toggle still outstanding |
| 4 | Apple Sign In (social) | Identity | Services ID + .p8 key **used by Auth0** | Apple Developer | Stored inside the Auth0 connection | **Credentials provisioned + Try Connection verified in dev 2026-08-10** (Services ID `com.codesistance.carditrack.mobile.signin`); **Applications → CardiTrack Mobile toggle enabled in dev 2026-08-10** — prod credentials + Try Connection + toggle still outstanding (Phase 9, below) |
| 7 | CardiTrack Actions | Identity | Confidential (M2M, Management API: `read:users` `update:users`) | Auth0 | **Action secrets only** (never Secret Manager, never the repo) | **Created + Action deployed in dev 2026-08-10** — [runbook §8a](./auth0_setup_runbook.md); powers the account-linking Action; prod still pending |
| 5 | Fitbit provider (Google Health API) | Device data | Confidential Web application | Google Cloud (`carditrack-devices-{env}`) | `devices-fitbit-client-id` / `devices-fitbit-client-secret` | **Provisioned 2026-08-07** — clients created, secrets loaded, API + Worker revisions rolled; field names verified against the v4 discovery document 2026-08-09; **live-wearer population check outstanding** (step 5b below) |
| 6+ | Garmin / Withings / Oura / Whoop | Device data | Per-vendor | Each vendor's portal | Not yet provisioned (`devices-{provider}-client-{id,secret}`) | Future — config stubs only; **only Fitbit is registered in DI** |

Device-data secrets are namespaced `devices-{provider}-client-{id,secret}` so each
new provider adds a matching pair rather than another bare `{vendor}-client-*`.

Related shared secret: `carditrack-{env}-encryption-key` (`Encryption__Key`) — the
AES-256-GCM key protecting the device-data tokens stored in `DeviceConnections`.
It belongs to no single OAuth client but every device-data flow depends on it.

> **The #3 vs #5 foot-gun:** both are "Google OAuth clients" under the same
> cloud-ops account, but they are different registrations with different
> purposes. #3 asks for `openid profile email` so a caregiver can *sign in*;
> #5 asks for restricted `googlehealth.*` scopes so a wearer can *share heart
> data*. Never reuse one for the other — mixing them would drag the sign-in
> client into Google's restricted-scope verification, and put health scopes on
> a login button. They live in **separate projects** precisely so this cannot
> happen by accident (below).

All non-Apple registrations live under the cloud-ops Google account
(cloudoperations@codesistance.com); Apple uses the same account's Apple
Developer membership.

## Google Cloud project layout

OAuth clients are split across four projects. The reason is one non-obvious
Google rule: **the consent screen, and its verification status, are per-project,
not per-client.** A project is either Testing or Published — so a single project
cannot host both a dev client that churns and a prod client whose verification
must stay intact.

| Project | Holds | Consent screen |
|---|---|---|
| `carditrack-490120` | All Terraform-managed infra, **dev and prod**: Cloud Run, Cloud SQL, Secret Manager, the deploy and Play-publisher service accounts. **No OAuth clients.** | none |
| `carditrack-signin` | `CardiTrack Sign-In (dev)` + `CardiTrack Sign-In (prod)` — client #3, used by Auth0 | `openid profile email` only; **Published** (branding review only, no user cap) |
| `carditrack-devices-dev` | `CardiTrack Devices (dev)` — client #5, dev | restricted `googlehealth.*`; **stays in Testing permanently**, test users only (max 100) |
| `carditrack-devices-prod` | `CardiTrack Devices (prod)` — client #5, prod | restricted `googlehealth.*`; **Testing — not yet submitted** (as of 2026-08-07). The only project that will *ever* be submitted for restricted-scope verification + CASA |

Consequences worth holding onto:

- **Both sign-in clients share one project** because their scopes and branding
  are identical — only the redirect URI differs, and that is per-client.
- **The two device projects cannot be merged.** Dev iteration (new redirect
  URIs, scope experiments, branding tweaks) on a Published project can put
  verification back under review — a ~4–8 week round trip to regain.
- **The device projects are shells**: one consent screen and one OAuth client
  each, no service accounts and no infra. The Play-publisher and GitHub deploy
  service accounts are never duplicated out of `carditrack-490120`.
- **Clients and their secrets live in different projects.** A Devices client is
  created in `carditrack-devices-{env}`, but its id/secret are stored in
  Secret Manager in `carditrack-490120` — pass `--project=carditrack-490120`
  when writing them.
- **Prod is in Testing too, for now.** `carditrack-devices-prod` has **not**
  been submitted for verification (as of 2026-08-07), so prod can only serve
  wearers explicitly listed as test users, max 100. Registering the client is
  not the same as being verified — public launch waits on step 6 below.

> **Console UI note:** Google now presents all of this as **Auth Platform**
> (left nav: *Overview · Branding · Audience · Clients · Data Access ·
> Verification Center*), not the single "OAuth consent screen" page that most
> third-party guides still describe. Scopes live under **Data Access**, test
> users and the Testing/Published switch under **Audience**, and clients under
> **Clients**.

---

## Social log-on (Phase 9) — scope

The mobile app renders **Google** and **Apple** buttons on **both**
`CreateAccountPage` and `SignInPage`, and the Auth0 Native app allows the
`carditrack://oauth/callback` callback and Authorization Code + PKCE grant
([runbook §3](./auth0_setup_runbook.md)). **As of 2026-08-10 the buttons are
wired**: both launch Auth0 Universal Login in the system browser
(`AuthService.SignInWithProviderAsync` → `connection=google-oauth2|apple`, code
+ PKCE + state, exchange at `/oauth/token`), then join the normal post-login
routing. **Google's credentials and Auth0 connection are done as of
2026-08-07**, so Google works as soon as the connection is enabled for
CardiTrack Mobile and the [runbook §8](./auth0_setup_runbook.md) Action is
deployed; Apple still needs its credentials — until then its button surfaces
"not available yet". Microsoft (`windowslive`) is not planned for MVP and has
no button in the mobile UI — treat it as deferred until product asks.

**What's needed per provider:**

### Google (`google-oauth2` connection) — provisioned

Done on 2026-08-07; recorded here because it must be repeated for any new Auth0
tenant. Both clients live in `carditrack-signin` (never the device projects).

1. Google Cloud console (cloud-ops account), project `carditrack-signin` →
   **Google Auth Platform → Clients → Create client**, type **Web application**,
   name `CardiTrack Sign-In ({env})` — a separate client from the Health API
   one (#5).
2. Authorized redirect URI: `https://{auth0-tenant-domain}/login/callback`
   (custom-domain tenants use that domain instead).
3. **Data Access**: only the non-sensitive scopes `openid`, `profile`, `email` —
   **no restricted-scope review applies to this client**; basic branding
   verification only. **Audience → Publish app** straight away: with no
   sensitive scopes there is nothing to review, and publishing removes the
   100-test-user cap that would otherwise throttle sign-in.
4. Auth0 dashboard → **Authentication → Social → Google** → paste that
   environment's client ID + secret, then on the connection's **Applications**
   tab enable it for **CardiTrack Mobile** (and Web when its login ships). The
   Applications toggle is load-bearing: with it off, a login carrying
   `connection=google-oauth2` is rejected even though the connection exists.
   **Try Connection** verifies the credentials before any app code exists.

> Auth0's built-in **dev keys** make the Google connection work with zero
> setup, but they show Auth0 branding on the consent screen, break SSO/silent
> auth, and are unsuitable beyond a smoke test. Leaving the client ID/secret
> fields empty silently falls back to them — so confirm both fields are
> populated in **every** tenant, not just dev.

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

**Status 2026-08-10**: Services ID `com.codesistance.carditrack.mobile.signin`
created and configured with both dev/prod return URLs; connection credentials
entered, Try Connection verified, and the **Applications → CardiTrack Mobile**
toggle enabled — all in the dev tenant. Still outstanding: repeating
credential entry, Try Connection, and the Applications toggle in the prod
tenant.

### Cross-cutting items (both providers)

- **Email verification**: social identities arrive pre-verified; the Action per
  [runbook §8](./auth0_setup_runbook.md) short-circuits the verification deny
  for `google-oauth2`/`apple` so social users aren't blocked by the gate.
- **Account linking — decided 2026-08-10**: linking happens **tenant-side in
  the post-login Action** ([runbook §8](./auth0_setup_runbook.md)). On the
  first social login whose verified email matches an existing **verified**
  database (`auth0|…`) account, the Action links the social identity into that
  primary and re-keys the session (`setPrimaryUser`), so the token `sub` — and
  the CardiTrack `Users.Auth0UserId` row — stays the database identity. The
  Action **fails open**; the API's safety net is a **409** from onboarding
  (`DuplicateEmailException`) whenever an email is already owned by a different
  `sub`, so an unlinked login can never fork a second account. The reverse
  order (social account first, password signup later) is deliberately *not*
  linked — the 409 message tells the user to sign in the way they first
  registered.
- **Per-environment**: everything above is per Auth0 tenant — repeat for dev
  and prod (including the `CardiTrack Actions` M2M app + Action secrets), and
  keep prod's Google/Apple credentials out of the repo (they live only in the
  Auth0 dashboard).

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
6. Social connections (Phase 9) — section above. Google is wired in both
   tenants as of 2026-08-07 and the app handlers shipped 2026-08-10; Apple
   credentials are still pending. Per tenant, also create the
   `CardiTrack Actions` M2M app and deploy the linking Action — runbook §8.

## Provisioning steps — device-data client (Google Health API)

> **Provisioned 2026-08-07.** Steps 1–4 are done in both environments: projects
> created, Google Health API enabled, consent screens configured, both
> `CardiTrack Devices` clients registered, secret values loaded and API + Worker
> revisions rolled. Step 5 is **half done**: field names are verified against the
> v4 discovery document (2026-08-09), but whether a real wearer's device
> populates each one is still outstanding and needs a live account. Step 6 gates
> public launch. The steps stay documented because they must be repeated for each
> new provider and any new environment.

Flow security notes: the `state` values in this flow are **opaque,
server-cached, single-use tokens with a 15-minute TTL** (never encode member
ids in them), and the bounce endpoint only ever redirects into the
`carditrack://` scheme — any other target would be an open redirect.

The bounce endpoint hands off to the app on **every** outcome, including a
denied consent, because the deep link is the only thing that dismisses the
in-app browser; a response that ends in the browser leaves the user on the
consent page with the app still waiting. It hands off with an HTML page that
calls `location.replace()` rather than a `Location:` header, since a redirect
naming a custom scheme is dropped by browsers and proxies that only forward
http(s). `prompt=consent` is sent **only while no refresh token is held** for
that member and provider (`FirstConsentAuthorizationParams`) — Google re-issues
a refresh token only when consent is shown again, but forcing it on every
connect makes a reconnect look like a failed one.

1. Google Cloud console (cloud-ops account), project `carditrack-devices-{env}`
   — one per environment, never shared (see the project layout above):
   **enable the Google Health API**. Do this **before** the consent screen: the
   restricted scopes do not appear in the Data Access picker until the API is
   enabled on the project.
2. **Consent screen** (**Google Auth Platform → Get started**, then
   **Branding**): External; app name `CardiTrack`; support email + branding
   matching the public homepage. **Data Access →** add the restricted scopes
   `googlehealth.activity_and_fitness.readonly`,
   `googlehealth.health_metrics_and_measurements.readonly`,
   `googlehealth.sleep.readonly`, `googlehealth.settings.readonly`.
   **`settings.readonly` was added after the first three** (it backs the
   paired-device battery reading, `PairedDevice.batteryLevel`): an existing
   project needs it added here before any wearer can grant it, and **wearers who
   connected earlier keep their original three-scope grant until they reconnect**,
   reporting no battery in the meantime. That degradation is by design and never
   fails a sync. **Audience →** add **test users** (dev/beta
   wearers' Google accounts) and leave the project in **Testing**: only listed
   accounts can connect, max 100. Dev stays in Testing permanently; prod leaves
   it only via step 6.
3. **Clients → Create client**, type **Web application**, name
   `CardiTrack Devices ({env})`. Authorized redirect URIs are the API's
   **bounce endpoint** — never the `carditrack://` deep link (Google rejects
   custom schemes on web clients):
   - dev: `https://localhost:7001/api/v1/oauth/redirect/fitbit` and the
     deployed dev API URL + `/api/v1/oauth/redirect/fitbit`
   - prod: `https://api.carditrack.com/api/v1/oauth/redirect/fitbit`
4. Populate Secret Manager. The secrets live in the **infra** project, not the
   device project the client was created in — hence the explicit `--project`:
   ```bash
   printf '%s' "$CLIENT_ID"     | gcloud secrets versions add carditrack-{env}-devices-fitbit-client-id     --data-file=- --project=carditrack-490120
   printf '%s' "$CLIENT_SECRET" | gcloud secrets versions add carditrack-{env}-devices-fitbit-client-secret --data-file=- --project=carditrack-490120
   ```
   then roll new API + Worker revisions so the env bindings pick up the values.
   **Ordering:** run `terraform apply` for the environment *before* loading
   values. Renaming a `placeholder_secrets` key destroys and recreates the
   secret shell, so a value written first would be discarded with it.
5. **Sandbox verification.** Two separable questions; the first is now closed.

   **(a) Are the field names right? — done 2026-08-09, no token required.**
   Every name, wire format and enum member `FitbitApiClient` reads was checked
   against the v4 **discovery document**
   (`https://health.googleapis.com/$discovery/rest?version=v4` — public, no
   auth), which is machine-readable and so settles spelling in a way the prose
   reference cannot. All six names flagged as inferred in issue #38 are
   confirmed correct. The pass also found two defects the prose reference had
   hidden, both silent-zero: `active-minutes` was filtered on another data
   type's enum members, and `sedentary-period`'s `durationSum` is a protobuf
   `Duration` (`"28800s"`) that was being parsed as a bare number. Both fixed,
   with the discovery-document method written up in the
   [probe README](../../tools/HealthApiProbe/README.md) so it is repeatable.

   **(b) Does a real wearer's device populate them? — still outstanding.**
   No schema can answer this, and the failure is silent: an unpopulated type and
   a misnamed field both come back null rather than throwing. With a test user
   connected, run the
   [Health API probe](../../tools/HealthApiProbe/README.md)
   (`dotnet run --project tools/HealthApiProbe`) against a day the wearer
   genuinely wore the device — it prints each response's field names beside what
   the client extracts, so the two cases separate. Not currently ticketed: issue
   #38 was closed once the names were settled, on the basis that a bug ticket
   gets raised if a live sync turns out to be missing a metric.

   **First live finding, 2026-08-10:** a wearer in continuous heart-rate
   tracking mode returned more than the granular fetch's 20,000-point cap for a
   single civil day — not a field-name bug, but the cap's underlying assumption
   (1-minute cadence, ~1,440 points/day) proven wrong by real device behaviour.
   Raised to 100,000 with paced pagination; see
   `FitbitApiClient.SampleSeriesCap` and the quota note in
   [data_sync_architecture.md](./data_sync_architecture.md).
6. **Before public launch**, in `carditrack-devices-prod` only — **not yet
   submitted as of 2026-08-07**: restricted-scope
   verification + CASA assessment — prerequisites checklist in
   [user_onboarding_process.md](./user_onboarding_process.md) (Step 6). Status:
   the Google-format in-app disclosure banner shipped on Web (PR #9); the
   mobile equivalent is still pending. `carditrack-devices-dev` is never
   submitted.

Future device providers (Garmin, Withings, …) repeat steps 3–4 in their own
portals with their own `DeviceProviders` entry and their own
`devices-{provider}-client-{id,secret}` pair; the multi-provider
config, keyed DI, and (if the vendor requires https redirects) the bounce
endpoint generalize as-is.
