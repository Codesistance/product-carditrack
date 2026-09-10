# Garmin Connect Developer Program: new-partner onboarding paused, no reopening date

**Severity:** FYI (roadmap intelligence, not a live-integration risk — see caveat below)
**Category:** devices

## Summary

Garmin has removed the access-request form for its Connect Developer Program (the
API surface exposing wearable health/activity data — distinct from Connect IQ,
which covers on-device watch apps and remains open). New API access requests
cannot currently be submitted; there is no published reopening date. Garmin's own
developer forum confirms via a program-team reply that the form was pulled and
new requests are paused with no ETA. Existing approved partners are unaffected.

## Why flagged

`docs/execution/backend/api/devices.md` and `.claude/commands/digest.md` both
note Garmin is configured in CardiTrack with a **placeholder client id** — it is
not yet a connectable provider (only `fitbit` and `pixel_watch`, both
GoogleHealth-backed, are actually wired up). Garmin news is explicitly "roadmap
intelligence, not a live integration risk" per the digest brief — nothing
currently shipped breaks. But this closes the door on a specific roadmap item:
if/when CardiTrack decides to activate the Garmin config block, there may be no
way to get a real (non-placeholder) client id and API approval at all right now.

## Sources

- Garmin Connect Developer Program FAQ (primary, could not be machine-fetched in
  this sandbox — egress to `developer.garmin.com` is blocked by the network
  proxy; confirm manually): https://developer.garmin.com/gc-developer-program/program-faq/
- Garmin's own developer forum, program-team reply confirming the pause:
  https://forums.garmin.com/apps-software/mobile-apps-web/f/garmin-connect-mobile-andriod/441607/garmin-connect-api-access-paused-for-months-what-are-startups-supposed-to-do/2054194
- Secondary summary (not sole source — corroborating only):
  https://www.themomentum.ai/blog/garmin-developer-program-closed-roadmap

## Open question

Is Garmin still worth carrying as a placeholder config, or should the roadmap
formally deprioritize it in favor of devices that arrive through the Google
Health API (where CardiTrack already has a working integration path)? Worth a
30-second check on `developer.garmin.com` next time someone touches the device
config, to see if the form has reopened.

---

claude "work through @research/queue/2026-09-10-garmin-developer-program-onboarding-paused.md"
