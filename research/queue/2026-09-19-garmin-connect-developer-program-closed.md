# Garmin Connect Developer Program's public application form removed, no ETA

**Severity:** HIGH
**Category:** devices

## Summary

Garmin froze new developer applications in spring 2026 following a lawsuit from Strava over API
terms, and — newly reported 2026-09-14 — has now removed the public developer-program
application form from developer.garmin.com entirely, replacing it with "Stay tuned for more
updates on the program," with no ETA given. Existing integrations are unaffected, and Connect IQ
(on-device apps installed on the Garmin watch itself) is untouched, but Connect IQ is not
CardiTrack's integration path — CardiTrack needs the server-side Connect Developer Program (OAuth
2.0 + PKCE, push webhooks) that `docs/execution/backend/api/devices.md` already flags as
"business-only and approval-gated."

CardiTrack's `garmin` provider is a config-only stub with a placeholder client id, earmarked for
R2 engine work, contingent on exactly this access. The docs already carried a caveat that "if
access does not come, Withings is the next engine" — this news confirms that condition is now
in effect, not just a risk.

## Sources

- https://the5krunner.com/2026/09/14/garmin-developer-api-access-paused/ (the5krunner, 2026-09-14 — primary trade-press source; Garmin itself has not published a dated announcement, only the removed/replaced application-form copy)

## Why flagged

This directly blocks a planned integration (Garmin, R2) with no visibility into when — or
whether — it reopens, and CardiTrack's own docs already named the fallback (Withings) contingent
on exactly this outcome.

## Question to answer next

Update `docs/execution/backend/api/devices.md`'s Garmin caveat from "approval-gated, apply before
scheduling R2" to "application channel closed indefinitely as of 2026-09-14" and re-sequence the
roadmap so Withings, not Garmin, is the next engine built. Set a recurring check (next digest
cycle plus monthly thereafter) on developer.garmin.com for the program reopening.

claude "work through @research/queue/2026-09-19-garmin-connect-developer-program-closed.md"
