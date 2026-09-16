# Garmin Connect Developer Program freeze traced to Strava lawsuit fallout — full program "redesign," no reopening ETA

**Severity:** FYI
**Category:** devices

## Summary

The Garmin developer-access freeze first noted 2026-09-05 (public application form
withdrawn, no ETA) now has a traceable cause: reporting links it to fallout from
Strava's since-dropped lawsuit over Garmin's July-2025 API brand-attribution
guidelines. Garmin has confirmed internally it is undertaking a "significant redesign
and modernisation" of the whole Connect Developer Program, with **no ETA for reopening
new applications**. Already-approved existing integrations continue to function; only
new partner applications are blocked. The application form remains removed from
garmin.com.

## Sources

- https://the5krunner.com/2026/09/14/garmin-developer-api-access-paused/ (specialist wearables-industry reporting)
- Corroborated by developer-forum reports of no reopening date (Garmin's own forums)

## Why flagged

CardiTrack's Garmin integration is config-only (placeholder client ID, no registered
engine) — this touches nothing live. But it deepens the risk case for the planned R2
Garmin engine: this is not a transient outage but a deliberate, open-ended program
overhaul tied to an unresolved legal/business dispute, which changes the R2 planning
assumption from "apply and wait a few weeks" to "no visibility into timeline." Withings
(the fallback engine, R4) remains open with no access changes found.

## Question to answer next

Does Garmin's "redesign" reintroduce stricter attribution or data-sharing terms once it
reopens (given the underlying dispute was over branding requirements)? If so, that could
affect CardiTrack's UI/attribution obligations if Garmin is still pursued as the R2
engine. In the meantime, treat Withings as the more likely near-term R2 candidate.

claude "work through @research/queue/2026-09-16-garmin-developer-program-freeze-strava-fallout.md"
