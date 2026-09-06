# Google ships "Health Guardian" — passive BP, insulin-resistance and breathing-emergency detection on Fitbit/Pixel Watch

**Severity:** HIGH
**Category:** devices

## Summary

Google announced "Health Guardian" (2026-08-12, so it predates our tracking window but
was never logged) — a suite shipping this fall to **Pixel Watch 3/4/5 and Fitbit Air**:
- Passive blood-pressure **trend** monitoring (pulse + motion patterns, not a clinical BP reading)
- Insulin-resistance trend detection (AI model trained on 1T+ minutes of opted-in user data; explicitly not a diagnostic tool)
- Sleep-breathing quality tracking
- Breathing-emergency detection (persistent/critical SpO2 drop — overdose, choking, severe pneumonia), launching first on Pixel Watch in select European countries

## Why this matters for CardiTrack

Fitbit and Pixel Watch are CardiTrack's **only two actually-connectable device types**
(both routed through the Google Health API v4, per `docs/execution/backend/api/devices.md`).
This is directly relevant two ways:
1. **Opportunity** — if these new trend/insight data types get exposed through the Google
   Health API's data-type catalog, CardiTrack could ingest them for richer
   caregiver-facing narration (a "sticky and ahead" feature candidate) without any new
   device-integration engineering.
2. **Competitive/platform risk** — Google is shipping consumer-facing health insights
   directly in the Fitbit/Pixel Watch apps. Anything CardiTrack's value-add currently
   covers (e.g. narrating a BP or breathing trend to a family caregiver) that Google now
   surfaces natively reduces CardiTrack's differentiation on those specific signals.

## Sources

- https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/ (official Google Health blog)

## Question to answer next

Check the Google Health API v4 data-types/discovery documentation (once reachable) for
whether Health Guardian's blood-pressure-trend, insulin-resistance-trend, or
breathing-emergency data types are (or will be) exposed via `health.googleapis.com` —
that determines whether this is ingestible by CardiTrack's existing `DeviceConnectionService`
or stays locked inside Google's own apps.

claude "work through @research/queue/2026-09-06-google-health-guardian-pixel-watch-fitbit-air.md"
