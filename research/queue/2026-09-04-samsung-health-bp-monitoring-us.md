# Samsung Health / Galaxy Watch blood-pressure monitoring live in the US

**Severity:** HIGH
**Category:** devices

## Summary

Samsung rolled out cuff-calibrated blood-pressure monitoring to US Galaxy Watch4+ users
(2026-03) via Samsung Health Monitor, and added a passive "Blood Pressure Trend" feature
in 2026-07.

## Sources

- https://www.samsungmobilepress.com/articles/samsung-health-blood-pressure-monitoring-us-galaxy-watch

## Why flagged

Roadmap intelligence, not a live risk: Samsung Health has **no config block at all** in
CardiTrack today (confirmed against `docs/execution/backend/api/devices.md` — it isn't
even a placeholder like Garmin/Withings). Given the size of the Galaxy Watch install
base plus this AFib+BP capability, this is the strongest signal yet to move Samsung
Health from "not started" to an actual roadmap line item.

## Question to answer next

Scope what a Samsung Health integration would require from scratch (Samsung Health API
access model, data-sharing partner program requirements, OAuth flow) since there is no
existing config to extend, unlike Garmin/Withings/Oura/Whoop.

claude "work through @research/queue/2026-09-04-samsung-health-bp-monitoring-us.md"

## Resolution — 2026-09-05

**No dedicated integration; via Google Health only.** Scoping confirmed there is nothing to
integrate server-side: the Samsung Health SDK for Android was deprecated 2025-07-31 and its
replacement, the Samsung Health Data SDK, runs only on the wearer's phone — Samsung offers no
third-party cloud API. An on-device path would make the wearer an app user, which the product
rules out. Galaxy Watch readings reach CardiTrack when the wearer shares Samsung Health into
Health Connect and lets the Google Health app read it; the live GoogleHealth engine then serves
them. Remaining work: add `GalaxyWatch` to the GoogleHealth block's `DeviceTypes` so the picker
records the brand, and verify which metrics Samsung actually passes through (blood pressure is
unlikely to be among them) with a live wearer. `HealthApi.SamsungHealth` and the seeded
`api.shealth.samsung.com` endpoint describe an API that does not exist. Same decision applied
to Apple Watch. Recorded in the release matrix (decision log #9) and `devices.md`. Closed.
