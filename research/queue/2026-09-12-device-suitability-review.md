# Recurring device suitability review — 2026-09-12

**Severity:** FYI
**Category:** devices
**Date found:** 2026-09-12

## Summary

Standing review of integrated vs. roadmap device suitability, per
`docs/execution/backend/api/devices.md` (last updated 2026-09-05, confirmed
unchanged today):

- **Live integrations (Fitbit, Pixel Watch via Google Health API):** no
  sunset or reliability risk this cycle. The legacy Fitbit Web API sunset
  (2026-09-30, tracked since 2026-09-04) is confirmed to affect CardiTrack
  **not at all** — CardiTrack has never called `api.fitbit.com`; all tokens
  are already Google OAuth against `health.googleapis.com`. No new cardiac
  scopes landed beyond what's already tracked.
- **Roadmap-only devices (Garmin, Withings — placeholder client IDs;
  Oura/Whoop — no provider mapping, dropped from roadmap 2026-09-05; Samsung
  Health — no config, will arrive only via the `via_google_health` route
  through the wearer's own Google Health app):** no developments this cycle
  change build priority. Garmin's developer program remains closed with no
  reopening signal. Withings' newer cardiac hardware (impedance
  cardiography, 6-lead ECG) is older CES 2026 news, not a fresh API
  capability.
- **Broader market:** real FDA/CE-cleared cardiac hardware exists (Aktiia
  Hilo, Biobeat, Circular Ring 2, HeartBeam, Cardiosense) but all clearances
  predate this review window — nothing new enough to reprioritize evaluation
  right now.
- **Watch item:** Google's "Health Guardian" (BP + insulin-resistance
  trends, tracked 2026-09-09) still has not landed in the Google Health API
  as of today — the consumer app UI (Fitbit Air / Pixel Watch app v5.07.2)
  is wired up, but the developer-facing API's June 2026 scope batch added
  ECG, irregular-rhythm-notification, core-body-temperature, and
  blood-glucose, not blood pressure. If BP/insulin-resistance scopes land in
  the API, that upgrades CardiTrack's existing Fitbit/Pixel Watch
  integration for free, with no new integration work.

## Sources

- `docs/execution/backend/api/devices.md` (internal, repo source of truth for
  integration status)
- Health Guardian follow-up: consumer rollout reporting dated 2026-09-11
  (app version v5.07.2); Google Health API data-types reference:
  https://developers.google.com/health/data-types

## Why flagged

This is the recurring device-suitability review requested alongside the
digest — reported even when nothing rises to a standalone item, so the
absence of change is itself the finding.

## Next question

Recheck after Fitbit Air's Health Guardian BP/insulin-resistance trends
actually ship to consumers — does the Google Health API add matching scopes
in the following weeks?
