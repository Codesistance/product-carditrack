# Google Health API v4 — legacy Fitbit Web API hard sunset, 2026-09-30

**Severity:** CRITICAL
**Category:** dependencies

## Summary

The legacy Fitbit Web API (`api.fitbit.com`) shuts down permanently on **2026-09-30** —
26 days from this run. All Fitbit data access moves exclusively to Google Health API v4
(`health.googleapis.com`) with Google OAuth 2.0. Critically, **existing Fitbit OAuth
tokens do not carry over** — every user who connected before the v4 cutover needs to
re-consent through the new Google OAuth flow before the deadline, or their Fitbit
connection goes dark.

CardiTrack's device integration is already built against Google Health API v4 per its
own design (`DeviceConnectionService`, `docs/execution/backend/api/devices.md`), so this
should be a non-event architecturally — but it is the single most load-bearing platform
dependency CardiTrack has (Fitbit + Pixel Watch are the only two connectable providers),
and the deadline is close enough that a "should be fine" needs verification, not
assumption.

## Sources

- https://developers.google.com/health/migration (Google's own migration guide — primary)
- https://community.fitbit.com/t5/Web-API-Development/Introducing-the-next-phase-of-the-Fitbit-Web-API/td-p/5821061 (corroborating vendor community post)

## Why flagged

- Dated breaking change (2026-09-30) on the platform CardiTrack's entire wearable-data
  path depends on exclusively.
- No token carryover means a population of already-connected users could silently lose
  sync if any re-consent flow gap exists.

## Question to answer next

Grep the codebase for any remaining reference to `api.fitbit.com`, a legacy Fitbit OAuth
client id/secret, or a stored legacy Fitbit token schema. Then confirm: for a
CardiMember who connected Fitbit before the v4 cutover, does `DeviceConnectionService`
already hold a Health-API-v4-issued token (in which case nothing to do), or does it hold
a legacy Fitbit token that needs a forced re-consent flow before 2026-09-30? If the
latter, is that flow shipped, and is there a way to identify affected users now?

claude "work through @research/queue/2026-09-04-google-health-fitbit-legacy-sunset.md"

## Resolution — 2026-09-05

**No exposure.** CardiTrack has never called `api.fitbit.com`. Grep of `src/`, `infrastructure/`
and `.github/` finds the string only in the original 2026-03 `InitialCreate` migration, as the
informational `ApiEndpoint` of the seeded Fitbit `Device` row — rewritten to
`health.googleapis.com` by the 2026-08-13 `AddGooglePixelWatchDevice` migration, and
`Device.ApiEndpoint` is never read at runtime. All three `appsettings.json` files and both
tfvars point `ApiBaseUrl` at `health.googleapis.com` and `AuthorizationUrl` at
`accounts.google.com`. Every stored `DeviceConnection` token was issued by Google OAuth against
the `carditrack-devices-{env}` client registered 2026-08-07, so there is no legacy-token
population and no forced re-consent to run.

Recorded in `docs/execution/backend/api/devices.md` ("Legacy Fitbit Web API — not exposed"),
`docs/technical/oauth_clients.md`, and the release matrix. The live-wearer field-population
check (oauth_clients.md step 5b) stays open on its own merits. Closed.
