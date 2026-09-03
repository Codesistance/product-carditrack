# WHOOP expands into FDA-cleared ECG + on-demand clinician access + proactive AI

**Severity:** HIGH
**Category:** competition
**Date flagged:** 2026-09-03

## Summary

WHOOP's own press release (2026-05-08) announces an FDA-cleared ECG, Blood Pressure Insights, and
Advanced Labs bloodwork, plus on-demand video consultations with licensed clinicians (via a
HealthEx EHR integration giving clinicians access to biometric history), alongside AI features
("My Memory" personalization and "Proactive Check-Ins" that flag schedule/travel-driven risk
changes).

## Sources

- https://www.whoop.com/us/en/press-center/whoop-expands-health-platform-with-on-demand-clinician-access-and-new-ai-features/

## Why flagged

WHOOP has no CardiTrack provider mapping today (no `provider` string maps to it — unreachable even
as a stub, per `docs/execution/backend/api/devices.md`), so this is roadmap intelligence, not a
live integration risk. But it is the clearest signal yet of a consumer wearable vendor assembling
CardiTrack's target feature set end-to-end in one platform: cardiac-grade hardware, AI-driven
proactive alerts, and a human clinician layer beyond CardiTrack's current caregiver-facing digest
model.

## Question to answer next

Is the clinician-video-visit + EHR-context pairing (a human-in-the-loop layer beyond
caregiver-facing narrative summaries) worth scoping as a CardiTrack differentiator or follow, and
does "Proactive Check-Ins" (context-aware risk flagging tied to schedule/travel) suggest a digest
enhancement CardiTrack could ship without new hardware integration work?

claude "work through @research/queue/2026-09-03-whoop-platform-expansion.md"
