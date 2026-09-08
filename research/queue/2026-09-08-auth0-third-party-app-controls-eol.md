# Auth0 deprecations: third-party-app security controls EOL 2026-10-23; new 10KB user-profile limit EOL 2027-03-04

**Severity:** HIGH
**Category:** dependencies

## Summary

Auth0's deprecations-and-migrations page lists two dated changes: (a)
"Enhanced security controls for third-party applications" (OAuth 2.1
alignment, mandatory PKCE) — deprecated 2026-04-23, end-of-life
**2026-10-23**; (b) a new 10KB size limit on Auth0 user-profile data
(`app_metadata`/`user_metadata`) — deprecated 2026-08-04, end-of-life
2027-03-04.

## Sources

- https://auth0.com/docs/troubleshoot/product-lifecycle/deprecations-and-migrations (Auth0 official deprecations index — primary; this is the index page, not an item-specific URL — Auth0 does not appear to publish a stable per-deprecation permalink for either entry)

## Why it matters to CardiTrack

CardiTrack uses Auth0 for the mobile/API JWT and OAuth path
(`Auth0ManagementClient.cs`, `Auth0Options`). A codebase search found no use
of `enabled_clients` or third-party-application registration patterns,
suggesting the (a) deprecation likely doesn't apply — but this was **not**
independently verified against the live Auth0 tenant configuration, only
against the source tree. The 6-week runway to 2026-10-23 makes this worth a
definitive check rather than an assumption. The 10KB profile-size limit (b)
is lower urgency (6 months out) but worth a quick check given
`User`/`CardiMember` carry growing free-text fields (`MedicalNotes`, chat
history references) that could theoretically bloat `app_metadata` if any of
that is ever mirrored there.

## Question to answer next

1. Confirm the Auth0 tenant's application type is *not* registered as a
   third-party application requiring the deprecated security controls —
   if it is, migrate before 2026-10-23.
2. Confirm no `user_metadata`/`app_metadata` payload is approaching 10KB
   (lower priority, EOL 2027-03-04).

claude "work through @research/queue/2026-09-08-auth0-third-party-app-controls-eol.md"
