# Samsung SmartThings "Family Care" — elderly/caregiver monitoring suite

**Severity:** HIGH
**Category:** competition
**Date flagged:** 2026-09-03

## Summary

Samsung's official CES 2026 release details SmartThings Family Care: fall detection,
cognitive-decline pattern alerts, medication/appointment reminders, and "Care on Call" caregiver
notifications, built across Samsung's wearable and appliance ecosystem and aimed at remote family
caregivers of elderly relatives.

## Sources

- https://news.samsung.com/global/ces-2026-a-care-companion-for-family-health-and-safety

## Why flagged

Samsung Health has no CardiTrack config block at all today (per `docs/execution/backend/api/devices.md`),
so there's no integration surface — this is roadmap/competitive intelligence, not a live risk.
But the feature set (severity-tiered caregiver alerts, family-facing health monitoring) directly
overlaps CardiTrack's core value proposition, from a much larger consumer-hardware ecosystem.

## Question to answer next

Benchmark CardiTrack's alert/digest UX against SmartThings Family Care's "Care on Call" flow —
specifically whether Samsung's cognitive-decline pattern alerting is a category CardiTrack should
consider (currently out of scope — CardiTrack is cardiac-focused).

claude "work through @research/queue/2026-09-03-samsung-smartthings-family-care.md"
