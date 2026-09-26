# Auth0 10 KB user-profile limit: deprecated 2026-08-04, end of life 2027-03-04 — primary page found after three days held

**Severity:** FYI
**Category:** dependencies
**Date recorded:** 2026-09-26

## Summary

Auth0 has deprecated the extended allowance for oversized user profiles. From the primary page
(indexed; auth0.com is proxy-blocked from the sandbox — two independent search queries agree on the
content): the serialised user profile, including `user_metadata` and `app_metadata`, is limited to
**10 KB**. Deprecated **2026-08-04**, end of life **2027-03-04**. Until then an extended allowance
(about ten times the limit, per the snippet) applies; writes above 10 KB but inside the allowance
succeed and emit a tenant-log warning. Tenants can opt in early via Dashboard > Tenant Settings >
Advanced > Migrations > "Uncapped User Profile Data". Log events fire only when a profile is updated
(a login, for example), so dormant oversized users go unreported.

**Where CardiTrack stands.** `grep -rn 'user_metadata\|app_metadata' src infrastructure` is empty. The
tenant's single post-login Action (`docs/technical/auth0_setup_runbook.md` §8) does verification
gating, adds claims and links social accounts; profiles carry identity basics only. This is almost
certainly a non-event. It is recorded because it is a hard, dated EOL on the identity provider on the
JWT path, and because it was held for three days for want of the primary URL, which is now known.

The deprecations index page (recorded 2026-09-20 for the third-party-app EOL of 2026-10-23) lists this
migration.

## Sources

- https://auth0.com/docs/troubleshoot/product-lifecycle/deprecations-and-migrations/migrate-oversized-user-profiles — the migration page (proxy-blocked; indexed)
- https://auth0.com/docs/troubleshoot/product-lifecycle/deprecations-and-migrations — the index (recorded 2026-09-20)

## Why flagged

Dated end of life on the identity provider. Recorded once, with the primary URL, so the deprecation
index's next change reads as an update rather than a rediscovery.

## Question to answer next

1. In the Auth0 tenant logs, search for the oversized-profile warning event type since 2026-08-04.
   If none, close with "no action". If any, identify what wrote the metadata.
2. Confirm from the tenant Dashboard that the "Uncapped User Profile Data" migration toggle is
   present and leave it at the default; there is nothing to gain from opting in early.

claude "work through @research/queue/2026-09-26-auth0-10kb-user-profile-limit-eol-2027-03-04.md"
