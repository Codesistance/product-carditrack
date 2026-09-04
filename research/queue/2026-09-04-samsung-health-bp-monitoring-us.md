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
