# Google Health API v4 added cardiac-relevant data-type scopes

**Severity:** HIGH
**Category:** devices

## Summary

Google Health API data-type documentation now lists scopes including
`electrocardiogram`, `irregular-rhythm-notification`, `blood-glucose`, and
`core-body-temperature`, alongside the existing heart-rate/HRV/SpO2 types.

## Sources

- https://developers.google.com/health/data-types

## Why flagged

This is a live-integration opportunity, not roadmap intelligence — it's on the exact
platform (Google Health API v4) CardiTrack already integrates through for its only two
connectable providers (Fitbit, Pixel Watch). If CardiTrack isn't already requesting
`electrocardiogram` / `irregular-rhythm-notification` scopes, native AFib signal from
already-connected devices could feed directly into the SSA anomaly pipeline or
corroborate its own heart-rate-based detections, with no new provider integration work.

## Question to answer next

Check `DeviceProviders` / the Google Health API scope list CardiTrack currently requests
at OAuth connect time (`docs/execution/backend/api/devices.md`, `DeviceConnectionService`).
Are `electrocardiogram` and `irregular-rhythm-notification` already requested? If not,
scope what it would take to add them and feed that data type into the existing SSA/
assessor pipeline.

claude "work through @research/queue/2026-09-04-google-health-api-cardiac-scopes.md"
