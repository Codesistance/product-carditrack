# Auth0 native-app callback deprecation — already live since 2026-04-28

**Severity:** CRITICAL
**Category:** dependencies

## Summary

For Auth0 tenants relying on custom-URI-scheme or loopback callbacks (the typical setup
for a native app using the OAuth Authorization Code Flow — which is how a MAUI app would
integrate Auth0), the old silent-return callback behavior ended on **2026-04-28**. Auth0
now inserts an **extra end-user login confirmation prompt** unless the app has migrated
to HTTPS-based callbacks via Android App Links / Apple Universal Links. This is not a
future deadline — it already changed in production for any non-migrated tenant.

## Sources

- https://auth0.com/docs/troubleshoot/product-lifecycle/deprecations-and-migrations/migrate-to-non-verifiable-callback-uri-end-user-confirmation (Auth0's own deprecation/migration doc — primary)

## Why flagged

CardiTrack's mobile client authenticates via Auth0 (confirmed by the `Auth0Domain` /
`Auth0ClientId` / `Auth0Audience` build properties passed into the MAUI Windows publish
step in CI, and the CLAUDE.md note that Auth0 is on the JWT auth path). If the mobile
Auth0 integration still uses a non-HTTPS custom-scheme callback (e.g.
`carditrack://callback`), users are seeing an unexpected extra confirmation screen on
every login *today*, which is a live UX regression, not a scheduled one.

## Question to answer next

Check the Auth0 application configuration for CardiTrack's mobile client(s): are the
allowed callback URLs HTTPS-based (Android App Links / Apple Universal Links), or
custom-scheme (`carditrack://...`)? If custom-scheme, confirm whether the extra
confirmation prompt has already been observed/reported, and scope the migration to
HTTPS-based callbacks.

claude "work through @research/queue/2026-09-04-auth0-native-callback-deprecation.md"
