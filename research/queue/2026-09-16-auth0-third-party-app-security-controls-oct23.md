# Auth0 mandates PKCE + explicit API authorization for third-party apps from 2026-10-23 — likely N/A to CardiTrack, needs one confirmation

**Severity:** FYI
**Category:** dependencies

## Summary

Auth0's "Enhanced Security Controls for Third-Party Applications" (OAuth 2.1-aligned:
mandatory PKCE, explicit API authorization) becomes the default for **newly-created**
third-party applications starting **2026-10-23** (37 days from this run). It only
applies to apps flagged as *third-party* in the tenant (i.e. apps built by external
developers against CardiTrack's Auth0 tenant, not CardiTrack's own first-party
MAUI/API clients) and only to apps created after that date with no
`third_party_security_mode` explicitly set. Existing apps are unaffected regardless of
type.

## Sources

- https://community.auth0.com/t/action-required-enhanced-security-controls-for-third-party-applications/202087 (Auth0 Community / official notice — primary)

## Why flagged

Worth a one-time confirmation rather than a compliance action: if none of CardiTrack's
Auth0 application registrations are marked third-party, this change is a non-event.

## Question to answer next

Confirm in the Auth0 tenant dashboard that none of CardiTrack's registered applications
(API, MAUI mobile client, any partner/B2B integration) are marked "third-party." If all
are first-party, this item can be closed with no action.

claude "work through @research/queue/2026-09-16-auth0-third-party-app-security-controls-oct23.md"
