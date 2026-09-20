# Auth0 enhanced security controls for third-party apps — EOL 2026-10-23 (33 days)

**Severity:** HIGH
**Category:** dependencies

## Summary

Auth0 is introducing enhanced OAuth 2.1-aligned security controls (mandatory PKCE,
explicit API authorization) for third-party applications. Deprecation announced
2026-04-23; end-of-life **2026-10-23**. From the EOL date, any *newly created* third-party
application (via `POST /api/v2/clients` without an explicit `third_party_security_mode`)
gets the strict controls applied automatically. This only affects tenants that had
third-party applications configured before 2026-04-23, and only new applications created
after EOL — existing third-party apps keep working unchanged. A second, unrelated
deprecation on the same page — a 10KB limit on user profile data (`user_metadata` /
`app_metadata`), deprecated 2026-08-04, EOL 2027-03-04 — is lower urgency (6 months out)
and only relevant if any CardiTrack metadata usage approaches that size.

## Sources

- https://auth0.com/docs/troubleshoot/product-lifecycle/deprecations-and-migrations (primary — Auth0's own deprecations index, confirmed via two independent lookups; direct fetch of this domain is proxy-blocked in this environment)

## Why flagged

Auth0 sits on CardiTrack's JWT/authentication path — explicitly on the digest's own
dependency watch list. A dated EOL inside 33 days on an identity provider warrants a
check even though CardiTrack's own Auth0 tenant almost certainly only holds first-party
applications (its own mobile/web/API clients), which this specific deprecation would not
touch. That assumption hasn't been verified against the actual tenant configuration.

## Question to answer next

Check the CardiTrack Auth0 tenant dashboard for any application registrations flagged as
third-party (as opposed to first-party). If none exist — the expected case — this is a
non-event and can be closed with no code change. If any do exist, decide whether the
default "strict" `third_party_security_mode` is acceptable before 2026-10-23 or whether an
explicit mode needs setting first.

claude "work through @research/queue/2026-09-20-auth0-third-party-app-oauth21-eol.md"
