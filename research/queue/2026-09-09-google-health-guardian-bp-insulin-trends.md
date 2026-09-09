# Google "Health Guardian" ships BP + insulin-resistance trends to Fitbit/Pixel Watch — not yet in the Health API

**Severity:** HIGH
**Category:** devices

## Summary

Google is rolling out "Health Guardian" cuffless Blood Pressure Trends and Insulin Resistance
Trends to Pixel Watch 3/4/5 and Fitbit (including the new Fitbit Air) through September 2026.
This lands directly on CardiTrack's only two live, connectable device types (`fitbit`,
`pixel_watch`, both via the GoogleHealth engine per `docs/execution/backend/api/devices.md`) —
a genuinely new cardiac-relevant signal on CardiTrack's load-bearing data path, not a
roadmap-only vendor.

The important caveat: as of this run, blood pressure has **no equivalent field yet in the
Google Health API v4** discovery document that `GoogleHealthApiClient`
(`src/Infrastructure/CardiTrack.Infrastructure/ExternalClients/GoogleHealthApiClient.cs`) checks
every field name and enum member against — the feature is visible in Google's own Health app
but not yet pullable by third-party integrators. This is a "watch and be ready" item, not an
integration task today.

## Sources

- https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/ (Google's own announcement, published 2026-08-12, feature rollout dated September 2026)

## Why flagged

New cardiac-relevant data type (blood pressure trend) on the exact hardware CardiTrack already
connects, with a real prospect of becoming API-accessible. If it lands in the Health API v4
discovery document, it's a natural, low-effort roadmap addition — CardiTrack already holds the
OAuth scopes and sync plumbing for these device types.

## Question to answer next

Watch the Google Health API v4 data-types/discovery document
(https://developers.google.com/health/data-types) for a blood-pressure or insulin-resistance
field appearing under the existing scope bundles. When it appears, scope the work to add it to
`DeviceSyncService`'s pulled data types — no new OAuth consent or engine work should be needed
since the scope bundle is presumably reused.

claude "work through @research/queue/2026-09-09-google-health-guardian-bp-insulin-trends.md"
