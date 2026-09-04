# Aktiia's Hilo Band — first FDA-cleared OTC cuffless blood-pressure wearable

**Severity:** FYI
**Category:** devices

## Summary

Aktiia received FDA clearance (2026-07) for the Hilo Band, the first cuffless,
PPG-based blood-pressure wearable authorized for over-the-counter sale, with continuous
day/night BP trending. 130k+ existing users; US retail launch in 2026.

## Sources

- https://www.biospace.com/press-releases/aktiias-hilo-band-becomes-first-cuffless-blood-pressure-monitor-cleared-by-fda-for-over-the-counter-use

## Why flagged

A genuinely new vendor with no existing CardiTrack config at all, offering a cardiac
feature (continuous, FDA-cleared cuffless BP) that none of CardiTrack's currently-
connected or stubbed providers have. Worth a scoping look as a differentiated
integration candidate, separate from the existing Garmin/Withings/Oura/Whoop/Samsung
backlog.

## Question to answer next

Determine whether Aktiia exposes a developer API/data-sharing program at all, and if so
whether it routes through Google Health API (in which case it may already be reachable
via the existing GoogleHealth engine) or requires a standalone integration.

claude "work through @research/queue/2026-09-04-aktiia-hilo-band-cuffless-bp.md"
