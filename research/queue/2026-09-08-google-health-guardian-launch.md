# Google ships "Health Guardian" — passive AI trend detection (blood pressure, insulin resistance, sleep breathing) on Fitbit/Pixel Watch, the exact devices CardiTrack connects to

**Severity:** HIGH
**Category:** devices

## Summary

Google announced "Health Guardian" on 2026-08-12: a suite of health-trend
features that runs passively on wearable sensor data already being collected
in the background. Three features were announced: (1) **blood pressure
trends**, which passively estimates BP trends from pulse and motion patterns
over time; (2) an **insulin resistance** signal, from an AI model Google says
was trained on "over 1 trillion minutes of data from 5 million opted-in
users" (Google is explicit this is not intended to diagnose or manage
diabetes); and (3) **sleep breathing** pattern trends. Rollout is "later this
fall" in the Google Health app, for **Pixel Watch 3, 4, 5, and Fitbit Air**.

## Sources

- https://blog.google/products-and-platforms/products/google-health/pixel-watch-health-guardian/ (Google's own blog — primary; note: this domain is currently unreachable from this sandbox's network egress, so this brief is built from the announcement's coverage — corroborating links below — rather than a direct fetch of the primary page; the URL itself is the correct citation and should be verified directly next time it's reachable)
- https://www.mobihealthnews.com/news/google-unveils-health-guardian-features-pixel-watch-fitbit (trade press, corroborating)
- https://gadgetsandwearables.com/2026/08/12/google-health-guardian-blood-pressure-insulin-resistance/ (trade press, corroborating, carries the announcement date)

## Why it matters to CardiTrack

Fitbit and Pixel Watch are **CardiTrack's only live device integrations** —
the GoogleHealth engine is the only one registered in DI
(`docs/execution/backend/api/devices.md`), and both device families are the
ones Health Guardian targets. Two distinct concerns:

1. **Competitive overlap.** Google is now shipping its own passive,
   population-scale AI trend narration (blood pressure trends, breathing
   pattern changes) directly into the OS-level Health app on the identical
   hardware CardiTrack pulls data from via the Health API. This sits close
   to CardiTrack's own value proposition of narrating wearable data for a
   family member/caregiver — Google is not a hypothetical competitor here,
   it is the platform CardiTrack depends on for data access.
2. **Possible new data surface.** If any of these trend outputs (blood
   pressure trend, breathing pattern) become exposed as new data types or
   fields via the Google Health API (`health.googleapis.com`) rather than
   staying Google Health app-exclusive, that would be a new, load-bearing
   data-type change per the standing watch on this API — CardiTrack would
   want to evaluate ingesting it rather than only recomputing the
   equivalent from raw sensor data.

Not a licensing or clinical-validity claim either way — Google explicitly
frames insulin resistance as non-diagnostic, consistent with the general
direction of wearable-AI framing this digest already tracks (MHRA AVT
guidance: automated *action* vs. human-reviewed *output* is the line).

## Question to answer next

Does "later this fall" 2026 rollout expose blood-pressure-trend or
sleep-breathing-trend data via the Google Health API's `dataTypes` (the same
discovery document/data-types page already on this digest's watch list), or
does it stay confined to the first-party Google Health app UI with no API
surface? If the former, evaluate ingesting it as a new CardiTrack signal
before a competitor-branded feature becomes the default expectation for
"what a smartwatch tells a caregiver."

claude "work through @research/queue/2026-09-08-google-health-guardian-launch.md"
