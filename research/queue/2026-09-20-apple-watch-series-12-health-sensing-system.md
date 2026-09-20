# Apple Watch Series 12 ships a new Health Sensing System — raises the stakes of CardiTrack's unverified Apple Watch bridge

**Severity:** HIGH
**Category:** devices

## Summary

Apple announced Watch Series 12 on 2026-09-09 (available 2026-09-18) with a new "Health
Sensing System": 5-second heart-rate sampling (up from the previous cadence), more
frequent HRV measurement, ECG-based AFib detection, and a daily Readiness Score computed
from overnight physiological signals.

## Sources

- https://www.apple.com/newsroom/2026/09/introducing-apple-watch-series-12-with-the-all-new-health-sensing-system/ (Apple Newsroom, primary)

## Why flagged

Per the 2026-09-05 product decision, CardiTrack will never build a dedicated Apple Watch
integration — Apple Health only exposes readings on-device, and the wearer is never a
CardiTrack app user. The entire path is: wearer shares Apple Watch data into the Google
Health app via Apple Health, and CardiTrack pulls it through the same Google Health API
used for Fitbit (`via_google_health`). `devices.md` already flags this as an **open,
unverified** question: "Nobody has yet run an Apple Watch ... through this route against
the field-population probe." Series 12's richer on-watch cardiac sensing doesn't change
CardiTrack's architecture, but it raises the cost of that verification gap staying open —
more caregiver households will be carrying this specific hardware and expecting its data to
show up.

## Question to answer next

Prioritize the still-outstanding field test: connect an Apple Watch Series 12 (or any
current-generation Apple Watch) through the Apple Health → Google Health app → CardiTrack
`via_google_health` path and confirm heart rate, steps and sleep — the metrics the alert
rules rely on — populate with usable fidelity. This is a verification task, not new
engineering work.

claude "work through @research/queue/2026-09-20-apple-watch-series-12-health-sensing-system.md"
