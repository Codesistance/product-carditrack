# Fitbit Web API decommission (Sept 2026) — possible mandatory re-consent for existing users

**Severity:** HIGH
**Category:** devices
**Date flagged:** 2026-09-03

## Summary

Google/Fitbit's own migration materials describe the legacy Fitbit Web API + its OAuth flow being
fully decommissioned in September 2026 (exact day not yet published), with the Google Health API
v4 as the replacement. Secondary indicators (a Google Health support-forum thread, the migration
overview page) suggest existing OAuth tokens do not carry over silently and every previously
connected user may need to re-consent through the new OAuth flow — this specific claim could not
be independently fetched/confirmed from this session's network (`developers.google.com` and
`support.google.com` were both blocked by the egress proxy). CardiTrack's own docs
(`docs/execution/backend/api/devices.md`, last updated 2026-08-13) already state the legacy API is
"decommissioned September 2026" and that CardiTrack's `fitbit`/`pixel_watch` providers already
route through the Google Health API — so the underlying pipeline is very likely already on the
correct path. What's unconfirmed is whether the sunset itself forces a mass re-authentication
event for CardiTrack's already-connected members.

## Sources

- https://developers.google.com/health/migration (not independently fetchable this session — egress blocked)
- https://support.google.com/googlehealth/thread/439040688 (not independently fetchable this session — egress blocked)
- https://developers.google.com/health/release-notes (v4 scope/endpoint additions — not independently fetchable this session)

## Why flagged

This is CardiTrack's only wearable ingestion path (`fitbit`/`pixel_watch` via GoogleHealth, the
only registered engine in DI per `devices.md`). If the September decommission forces re-consent
for already-connected members, that is a caregiver-facing event (a re-auth prompt, possibly a
temporary sync gap) that should be planned and communicated, not discovered when connections start
failing.

## Question to answer next

From a network that can reach `developers.google.com`, confirm: (1) the exact September 2026
decommission date, (2) whether it affects connections already made through the Google Health API
(vs. only connections still on the legacy Fitbit Web API path), and (3) whether CardiTrack's
current OAuth scopes and `pairedDevices` handling match the v4 shape described in the release
notes (new scopes: `active-energy-burned`, `electrocardiogram`, `core-body-temperature`,
`blood-glucose`, `irregular-rhythm-notification`).

claude "work through @research/queue/2026-09-03-google-health-api-fitbit-migration-reconsent.md"
